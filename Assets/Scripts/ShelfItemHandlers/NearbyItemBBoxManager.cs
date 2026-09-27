using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

public class NearbyItemBBoxManager : MonoBehaviour
{
    private sealed class StackGroup
    {
        public readonly int id;
        public readonly List<VirtualItemBBoxRecord> records = new();
        public ShelfItemPhysicsStack activeStack;

        public StackGroup(int id)
        {
            this.id = id;
        }
    }

    private static readonly ProfilerMarker RegisterMarker =
        new ProfilerMarker("NearbyItemBBox.RegisterVirtual");
    private static readonly ProfilerMarker ActivationTickMarker =
        new ProfilerMarker("NearbyItemBBox.ActivationTick");
    private static readonly ProfilerMarker MaterializeMarker =
        new ProfilerMarker("NearbyItemBBox.Materialize");
    private static readonly ProfilerMarker ReleaseMarker =
        new ProfilerMarker("NearbyItemBBox.Release");

    private static NearbyItemBBoxManager _instance;

    [SerializeField] private float tickInterval = 0.15f;
    [SerializeField] private float activationRadius = 3.0f;
    [SerializeField] private float deactivateRadius = 3.5f;
    [SerializeField] private float cellSize = 1.0f;

    [Header("Debug")]
    [SerializeField] private int totalVirtualBBoxes;
    [SerializeField] private int activeRealBBoxes;
    [SerializeField] private int pooledBBoxes;
    [SerializeField] private int createdThisTick;
    [SerializeField] private int releasedThisTick;

    private readonly Dictionary<ItemSpawner, HashSet<VirtualItemBBoxRecord>> _recordsByOwner = new();
    private int _recordCount;
    private readonly Dictionary<Vector2Int, List<VirtualItemBBoxRecord>> _grid = new();
    private readonly Dictionary<int, StackGroup> _stackGroups = new();
    private readonly HashSet<VirtualItemBBoxRecord> _activeRecords = new();
    private readonly Stack<ItemBBoxInfo> _pool = new();
    private readonly Dictionary<string, HashSet<InstanceData>> _pendingGpuRemovals = new();
    private readonly List<Transform> _activationOrigins = new();

    private readonly List<Vector3> _originPositions = new();
    private readonly HashSet<VirtualItemBBoxRecord> _candidateRecords = new();
    private readonly List<VirtualItemBBoxRecord> _activeSnapshot = new();
    private readonly HashSet<int> _processedStackGroups = new();

    private Transform _activeRoot;
    private Transform _poolRoot;
    private float _nextTickTime;
    private int _nextStackGroupId;

    public int TotalVirtualBBoxes => totalVirtualBBoxes;
    public int ActiveRealBBoxes => activeRealBBoxes;
    public int PooledBBoxes => pooledBBoxes;

    public static NearbyItemBBoxManager Instance
    {
        get
        {
            if (_instance != null) return _instance;

            _instance = FindFirstObjectByType<NearbyItemBBoxManager>();
            if (_instance != null) return _instance;

            GameObject go = new GameObject(nameof(NearbyItemBBoxManager));
            _instance = go.AddComponent<NearbyItemBBoxManager>();
            return _instance;
        }
    }

    // Non-creating lookup; the manager registers itself in Awake, so no scene search is needed.
    public static NearbyItemBBoxManager TryGetInstance() => _instance != null ? _instance : null;

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        EnsureRoots();
        UpdateDebugCounters();
    }

    private void OnDestroy()
    {
        if (_instance == this)
            _instance = null;
    }

    private void OnValidate()
    {
        tickInterval = Mathf.Max(0.02f, tickInterval);
        activationRadius = Mathf.Max(0.1f, activationRadius);
        deactivateRadius = Mathf.Max(activationRadius, deactivateRadius);
        cellSize = Mathf.Max(0.1f, cellSize);
    }

    private void Update()
    {
        if (Time.time < _nextTickTime) return;

        _nextTickTime = Time.time + tickInterval;
        Tick();
    }

    public void RegisterVirtualBBox(VirtualItemBBoxRecord record)
    {
        if (record == null || record.consumed || record.ownerSpawner is null) return;

        using (RegisterMarker.Auto())
        {
            if (!_recordsByOwner.TryGetValue(record.ownerSpawner, out HashSet<VirtualItemBBoxRecord> ownerRecords))
            {
                ownerRecords = new HashSet<VirtualItemBBoxRecord>();
                _recordsByOwner[record.ownerSpawner] = ownerRecords;
            }

            if (!ownerRecords.Add(record)) return;
            _recordCount++;
            record.gridCell = GetCell(record.bboxCenter);

            if (!_grid.TryGetValue(record.gridCell, out List<VirtualItemBBoxRecord> cellRecords))
            {
                cellRecords = new List<VirtualItemBBoxRecord>();
                _grid[record.gridCell] = cellRecords;
            }

            cellRecords.Add(record);
            UpdateDebugCounters();
        }
    }

    public void RegisterStackGroup(IReadOnlyList<VirtualItemBBoxRecord> records)
    {
        if (records == null || records.Count == 0) return;

        int groupId = ++_nextStackGroupId;
        StackGroup group = new StackGroup(groupId);

        for (int i = 0; i < records.Count; i++)
        {
            VirtualItemBBoxRecord record = records[i];
            if (record == null || record.consumed) continue;

            record.stackGroupId = groupId;
            group.records.Add(record);
        }

        if (group.records.Count > 0)
            _stackGroups[groupId] = group;
    }

    public void RegisterActivationOrigin(Transform origin)
    {
        if (origin == null || _activationOrigins.Contains(origin)) return;

        _activationOrigins.Add(origin);
    }

    public void UnregisterActivationOrigin(Transform origin)
    {
        if (origin == null) return;

        _activationOrigins.Remove(origin);
    }

    public void NotifyShelfBBoxDestroyed(ItemBBoxInfo info) => ConsumeRecord(info, suppressGpuRemoval: false);

    public void NotifyBBoxBecameDropped(ItemBBoxInfo info) => ConsumeRecord(info, suppressGpuRemoval: true);

    public void ClearAllVirtualBBoxes(bool removeGpuInstances)
    {
        foreach (HashSet<VirtualItemBBoxRecord> ownerRecords in _recordsByOwner.Values)
            foreach (VirtualItemBBoxRecord record in ownerRecords)
                ReleaseRegisteredRecord(record, removeGpuInstances);

        FlushGpuRemovals();
        _recordsByOwner.Clear();
        _recordCount = 0;
        _grid.Clear();
        _stackGroups.Clear();
        _activeRecords.Clear();
        DestroyPooledObjects();
        UpdateDebugCounters();
    }

    public void ClearOwner(ItemSpawner owner, bool removeGpuInstances)
    {
        if (owner is null || !_recordsByOwner.Remove(owner, out HashSet<VirtualItemBBoxRecord> ownerRecords))
            return;

        foreach (VirtualItemBBoxRecord record in ownerRecords)
        {
            ReleaseRegisteredRecord(record, removeGpuInstances);
            RemoveFromSpatialIndex(record);
            PruneStackGroup(record.stackGroupId);
        }

        FlushGpuRemovals();
        _recordCount -= ownerRecords.Count;
        UpdateDebugCounters();
    }

    private void ConsumeRecord(ItemBBoxInfo info, bool suppressGpuRemoval)
    {
        if (info == null || info.VirtualRecord == null) return;

        VirtualItemBBoxRecord record = info.VirtualRecord;
        record.activeBBoxInfo = null;
        record.consumed = true;
        info.VirtualRecord = null;
        if (suppressGpuRemoval)
            info.suppressGpuRemovalOnDestroy = true;
        _activeRecords.Remove(record);
        RemoveRecordFromRegistry(record);
        PruneStackGroup(record.stackGroupId);
        UpdateDebugCounters();
    }

    private void Tick()
    {
        createdThisTick = 0;
        releasedThisTick = 0;

        using (ActivationTickMarker.Auto())
        {
            CollectOriginPositions();
            ActivateNearbyRecords();
            ReleaseFarRecords();
            UpdateDebugCounters();
        }
    }

    private void ActivateNearbyRecords()
    {
        if (_originPositions.Count == 0) return;

        _candidateRecords.Clear();
        int cellRange = Mathf.CeilToInt(activationRadius / cellSize);

        for (int i = 0; i < _originPositions.Count; i++)
        {
            Vector2Int originCell = GetCell(_originPositions[i]);
            for (int x = originCell.x - cellRange; x <= originCell.x + cellRange; x++)
            {
                for (int y = originCell.y - cellRange; y <= originCell.y + cellRange; y++)
                {
                    Vector2Int cell = new Vector2Int(x, y);
                    if (!_grid.TryGetValue(cell, out List<VirtualItemBBoxRecord> cellRecords))
                        continue;

                    for (int r = 0; r < cellRecords.Count; r++)
                        _candidateRecords.Add(cellRecords[r]);
                }
            }
        }

        float activationSqr = activationRadius * activationRadius;
        foreach (VirtualItemBBoxRecord record in _candidateRecords)
        {
            if (record == null || record.consumed || record.IsActive) continue;
            if (MinSqrDistanceToOrigins(record.bboxCenter) > activationSqr) continue;

            if (record.stackGroupId >= 0 &&
                _stackGroups.TryGetValue(record.stackGroupId, out StackGroup group))
            {
                ActivateStackGroup(group);
            }
            else
            {
                MaterializeRecord(record);
            }
        }
    }

    private void ReleaseFarRecords()
    {
        _activeSnapshot.Clear();
        foreach (VirtualItemBBoxRecord record in _activeRecords)
            _activeSnapshot.Add(record);

        _processedStackGroups.Clear();
        float deactivateSqr = deactivateRadius * deactivateRadius;

        for (int i = 0; i < _activeSnapshot.Count; i++)
        {
            VirtualItemBBoxRecord record = _activeSnapshot[i];
            if (record == null || record.activeBBoxInfo == null)
            {
                _activeRecords.Remove(record);
                continue;
            }

            if (record.stackGroupId >= 0 &&
                _stackGroups.TryGetValue(record.stackGroupId, out StackGroup group))
            {
                if (!_processedStackGroups.Add(group.id)) continue;
                if (IsStackGroupWithinRadius(group, deactivateSqr)) continue;

                TryReleaseStackGroup(group);
                continue;
            }

            if (MinSqrDistanceToOrigins(record.bboxCenter) <= deactivateSqr) continue;

            TryReleaseRecord(record, checkStack: true);
        }
    }

    private void ActivateStackGroup(StackGroup group)
    {
        if (group == null || group.activeStack != null) return;

        List<ItemBBoxInfo> activeMembers = new List<ItemBBoxInfo>(group.records.Count);
        for (int i = 0; i < group.records.Count; i++)
        {
            VirtualItemBBoxRecord record = group.records[i];
            if (record == null || record.consumed) continue;

            if (record.activeBBoxInfo == null)
                MaterializeRecord(record);

            if (record.activeBBoxInfo != null)
                activeMembers.Add(record.activeBBoxInfo);
        }

        if (activeMembers.Count > 0)
            group.activeStack = new ShelfItemPhysicsStack(activeMembers);
    }

    private bool TryReleaseStackGroup(StackGroup group)
    {
        if (group == null) return true;
        if (group.activeStack != null && !group.activeStack.CanReturnToVirtualPool)
            return false;

        for (int i = 0; i < group.records.Count; i++)
        {
            ItemBBoxInfo info = group.records[i]?.activeBBoxInfo;
            if (info != null && !CanReleaseShelfBBox(info, checkStack: false))
                return false;
        }

        group.activeStack?.ClearVirtualPoolMembers();
        group.activeStack = null;

        bool releasedAll = true;
        for (int i = 0; i < group.records.Count; i++)
            releasedAll &= TryReleaseRecord(group.records[i], checkStack: false);

        return releasedAll;
    }

    private void MaterializeRecord(VirtualItemBBoxRecord record)
    {
        if (record == null || record.consumed || record.activeBBoxInfo != null) return;

        using (MaterializeMarker.Auto())
        {
            ItemBBoxInfo info = GetBBoxFromPool();
            GameObject bbox = info.gameObject;
            BoxCollider boxCollider = info.BoxCollider;
            Renderer renderer = info.Renderer;
            OutlineFx.OutlineFx outlineFx = info.OutlineFx;
            OutlineController outlineController = info.OutlineController;
            ItemBBoxPhysicsProxy proxy = info.Proxy;

            bbox.SetActive(false);
            bbox.name = record.itemId + "_VirtualBBox";
            bbox.transform.SetParent(_activeRoot, worldPositionStays: false);
            bbox.transform.SetPositionAndRotation(record.bboxCenter, Quaternion.identity);
            bbox.transform.localScale = record.bboxSize;

            if (renderer != null)
            {
                renderer.enabled = true;
                if (record.bboxMaterial != null)
                    renderer.sharedMaterial = record.bboxMaterial;
            }

            if (boxCollider != null)
            {
                boxCollider.enabled = true;
                boxCollider.name = record.itemId;
                boxCollider.isTrigger = true;
                boxCollider.size = Vector3.one;
            }

            if (outlineFx != null)
                outlineFx.enabled = false;
            if (outlineController != null)
                outlineController.ResetOutlineState();

            info.ClearItemState();
            info.suppressGpuRemovalOnDestroy = false;
            info.itemId = record.itemId;
            info.expirationDateDecalId = record.expirationDateDecalId;
            info.instanceData = record.instanceData;
            info.physicsSpawnPosition = record.physicsSpawnPosition;
            info.spawnRotation = record.spawnRotation;
            info.VirtualRecord = record;

            bool enableShelfPhysics =
                DataHandler.Instance != null && DataHandler.Instance.enableShelfItemPhysics;
            if (proxy != null)
                proxy.ResetForVirtualPoolReuse(enableShelfPhysics);

            record.activeBBoxInfo = info;
            _activeRecords.Add(record);
            createdThisTick++;
            bbox.SetActive(true);
        }
    }

    private bool TryReleaseRecord(VirtualItemBBoxRecord record, bool checkStack)
    {
        if (record == null || record.activeBBoxInfo == null) return true;

        ItemBBoxInfo info = record.activeBBoxInfo;
        if (!CanReleaseShelfBBox(info, checkStack)) return false;

        using (ReleaseMarker.Auto())
        {
            GameObject bbox = info.gameObject;
            ItemBBoxPhysicsProxy proxy = info.Proxy;
            OutlineController outlineController = info.OutlineController;
            OutlineFx.OutlineFx outlineFx = info.OutlineFx;
            BoxCollider boxCollider = info.BoxCollider;
            Renderer renderer = info.Renderer;

            if (proxy != null) proxy.ResetForVirtualPoolRelease();
            if (outlineController != null) outlineController.ResetOutlineState();
            if (outlineFx != null) outlineFx.enabled = false;
            if (boxCollider != null) boxCollider.enabled = false;
            if (renderer != null) renderer.enabled = false;

            info.ClearItemState();
            info.suppressGpuRemovalOnDestroy = true;

            record.activeBBoxInfo = null;
            _activeRecords.Remove(record);

            bbox.name = "PooledItemBBox";
            bbox.transform.SetParent(_poolRoot, worldPositionStays: false);
            bbox.SetActive(false);
            _pool.Push(info);
            releasedThisTick++;
            return true;
        }
    }

    private bool CanReleaseShelfBBox(ItemBBoxInfo info, bool checkStack)
    {
        if (info == null || info.isPhysicsObject || info.returnToPoolOnDelete)
            return false;

        if (info.transform.parent != _activeRoot)
            return false;

        ItemBBoxPhysicsProxy proxy = info.Proxy;
        if (proxy != null && !proxy.CanReturnToVirtualPool)
            return false;

        if (checkStack && info.PhysicsStack != null && !info.PhysicsStack.CanReturnToVirtualPool)
            return false;

        return true;
    }

    // Consumes the record and destroys its live bbox; callers own registry removal and FlushGpuRemovals.
    private void ReleaseRegisteredRecord(VirtualItemBBoxRecord record, bool removeGpuInstances)
    {
        if (record == null) return;

        record.consumed = true;
        if (removeGpuInstances)
            QueueGpuRemoval(record);

        if (record.stackGroupId >= 0 &&
            _stackGroups.TryGetValue(record.stackGroupId, out StackGroup group))
        {
            group.activeStack?.ClearVirtualPoolMembers();
            group.activeStack = null;
        }

        ItemBBoxInfo info = record.activeBBoxInfo;
        record.activeBBoxInfo = null;
        _activeRecords.Remove(record);

        if (info != null)
        {
            ItemBBoxPhysicsProxy proxy = info.Proxy;
            if (proxy != null) proxy.ReleasePreviewForVirtualCleanup();

            info.suppressGpuRemovalOnDestroy = true;
            info.PhysicsStack = null;
            info.VirtualRecord = null;
            Destroy(info.gameObject);
        }
    }

    private void RemoveRecordFromRegistry(VirtualItemBBoxRecord record)
    {
        if (record == null) return;

        RemoveFromSpatialIndex(record);
        if (!(record.ownerSpawner is null) &&
            _recordsByOwner.TryGetValue(record.ownerSpawner, out HashSet<VirtualItemBBoxRecord> ownerRecords) &&
            ownerRecords.Remove(record))
        {
            _recordCount--;
            if (ownerRecords.Count == 0)
                _recordsByOwner.Remove(record.ownerSpawner);
        }
    }

    private void RemoveFromSpatialIndex(VirtualItemBBoxRecord record)
    {
        if (record == null) return;

        if (!_grid.TryGetValue(record.gridCell, out List<VirtualItemBBoxRecord> cellRecords))
            return;

        cellRecords.Remove(record);
        if (cellRecords.Count == 0)
            _grid.Remove(record.gridCell);
    }

    private void QueueGpuRemoval(VirtualItemBBoxRecord record)
    {
        if (string.IsNullOrEmpty(record.itemId)) return;

        if (!_pendingGpuRemovals.TryGetValue(record.itemId, out HashSet<InstanceData> removals))
        {
            removals = new HashSet<InstanceData>();
            _pendingGpuRemovals[record.itemId] = removals;
        }

        removals.Add(record.instanceData);
    }

    // One bulk removal per batch instead of a linear search per record.
    private void FlushGpuRemovals()
    {
        if (_pendingGpuRemovals.Count == 0) return;

        GPUInstanceTracker tracker = GPUInstanceTracker.Instance;
        if (tracker != null)
        {
            foreach (KeyValuePair<string, HashSet<InstanceData>> kvp in _pendingGpuRemovals)
            {
                BatchInstancer batch = tracker.GetBatchInstancerFromId(kvp.Key);
                if (batch != null) batch.RemoveDrawData(kvp.Value);
            }
        }

        _pendingGpuRemovals.Clear();
    }

    // Drops the stack group once none of its records are live.
    private void PruneStackGroup(int groupId)
    {
        if (groupId < 0 || !_stackGroups.TryGetValue(groupId, out StackGroup group)) return;

        for (int i = 0; i < group.records.Count; i++)
        {
            if (group.records[i] != null && !group.records[i].consumed)
                return;
        }

        _stackGroups.Remove(groupId);
    }

    private bool IsStackGroupWithinRadius(StackGroup group, float sqrRadius)
    {
        if (group == null) return false;

        for (int i = 0; i < group.records.Count; i++)
        {
            VirtualItemBBoxRecord record = group.records[i];
            if (record == null || record.consumed) continue;
            if (MinSqrDistanceToOrigins(record.bboxCenter) <= sqrRadius)
                return true;
        }

        return false;
    }

    private float MinSqrDistanceToOrigins(Vector3 point)
    {
        if (_originPositions.Count == 0) return float.PositiveInfinity;

        float min = float.PositiveInfinity;
        for (int i = 0; i < _originPositions.Count; i++)
        {
            Vector3 delta = point - _originPositions[i];
            float sqr = delta.sqrMagnitude;
            if (sqr < min)
                min = sqr;
        }

        return min;
    }

    private void CollectOriginPositions()
    {
        _originPositions.Clear();

        for (int i = _activationOrigins.Count - 1; i >= 0; i--)
        {
            Transform origin = _activationOrigins[i];
            if (origin == null)
            {
                _activationOrigins.RemoveAt(i);
                continue;
            }

            _originPositions.Add(origin.position);
        }

        if (_originPositions.Count > 0) return;

        Camera camera = GPUInstanceTracker.Instance != null ? GPUInstanceTracker.Instance.MainCamera : null;
        if (camera == null) camera = Camera.main;
        if (camera != null)
        {
            _originPositions.Add(camera.transform.position);
            return;
        }

        if (DataHandler.Instance != null)
            _originPositions.Add(DataHandler.Instance.AgentPosition);
    }

    private ItemBBoxInfo GetBBoxFromPool()
    {
        EnsureRoots();

        while (_pool.Count > 0)
        {
            ItemBBoxInfo pooled = _pool.Pop();
            if (pooled != null) return pooled;
        }

        return ItemBBoxInfo.CreateBBoxObject("PooledItemBBox", outlineEnabled: true, addPhysicsProxy: true);
    }

    private Vector2Int GetCell(Vector3 position)
    {
        return new Vector2Int(
            Mathf.FloorToInt(position.x / cellSize),
            Mathf.FloorToInt(position.z / cellSize));
    }

    private void EnsureRoots()
    {
        if (_activeRoot == null)
        {
            GameObject activeRoot = new GameObject("ActiveItemBBoxes");
            activeRoot.transform.SetParent(transform, worldPositionStays: false);
            _activeRoot = activeRoot.transform;
        }

        if (_poolRoot == null)
        {
            GameObject poolRoot = new GameObject("PooledItemBBoxes");
            poolRoot.transform.SetParent(transform, worldPositionStays: false);
            _poolRoot = poolRoot.transform;
        }
    }

    private void DestroyPooledObjects()
    {
        while (_pool.Count > 0)
        {
            ItemBBoxInfo pooled = _pool.Pop();
            if (pooled == null) continue;

            pooled.suppressGpuRemovalOnDestroy = true;
            pooled.VirtualRecord = null;
            Destroy(pooled.gameObject);
        }
    }

    private void UpdateDebugCounters()
    {
        totalVirtualBBoxes = _recordCount;
        activeRealBBoxes = _activeRecords.Count;
        pooledBBoxes = _pool.Count;
    }
}
