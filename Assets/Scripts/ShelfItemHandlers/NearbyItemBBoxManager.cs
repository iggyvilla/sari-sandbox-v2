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

    private readonly List<VirtualItemBBoxRecord> _records = new();
    private readonly Dictionary<Vector2Int, List<VirtualItemBBoxRecord>> _grid = new();
    private readonly Dictionary<int, StackGroup> _stackGroups = new();
    private readonly HashSet<VirtualItemBBoxRecord> _activeRecords = new();
    private readonly Stack<GameObject> _pool = new();
    private readonly List<Transform> _activationOrigins = new();

    private readonly List<Vector3> _originPositions = new();
    private readonly HashSet<VirtualItemBBoxRecord> _candidateRecords = new();
    private readonly List<VirtualItemBBoxRecord> _activeSnapshot = new();
    private readonly HashSet<int> _processedStackGroups = new();

    private Transform _activeRoot;
    private Transform _poolRoot;
    private float _nextTickTime;
    private int _nextRecordId;
    private int _nextStackGroupId;
    private int _itemBBoxLayer;

    public int TotalVirtualBBoxes => totalVirtualBBoxes;
    public int ActiveRealBBoxes => activeRealBBoxes;
    public int PooledBBoxes => pooledBBoxes;
    public int CreatedThisTick => createdThisTick;
    public int ReleasedThisTick => releasedThisTick;

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

    public static NearbyItemBBoxManager TryGetInstance()
    {
        if (_instance != null) return _instance;

        _instance = FindFirstObjectByType<NearbyItemBBoxManager>();
        return _instance;
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        _itemBBoxLayer = LayerMask.NameToLayer("ItemBBox");
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
        if (record == null || record.consumed) return;

        using (RegisterMarker.Auto())
        {
            record.recordId = ++_nextRecordId;
            record.gridCell = GetCell(record.bboxCenter);
            _records.Add(record);

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

    public void NotifyShelfBBoxDestroyed(ItemBBoxInfo info)
    {
        if (info == null || info.VirtualRecord == null) return;

        VirtualItemBBoxRecord record = info.VirtualRecord;
        record.activeBBoxInfo = null;
        record.consumed = true;
        info.VirtualRecord = null;
        _activeRecords.Remove(record);
        RemoveRecordFromRegistry(record);
        PruneEmptyStackGroups();
        UpdateDebugCounters();
    }

    public void NotifyBBoxBecameDropped(ItemBBoxInfo info)
    {
        if (info == null || info.VirtualRecord == null) return;

        VirtualItemBBoxRecord record = info.VirtualRecord;
        record.activeBBoxInfo = null;
        record.consumed = true;
        info.VirtualRecord = null;
        info.suppressGpuRemovalOnDestroy = true;
        _activeRecords.Remove(record);
        RemoveRecordFromRegistry(record);
        PruneEmptyStackGroups();
        UpdateDebugCounters();
    }

    public void ClearAllVirtualBBoxes(bool removeGpuInstances)
    {
        for (int i = _records.Count - 1; i >= 0; i--)
            ClearRegisteredRecord(_records[i], removeGpuInstances);

        _records.Clear();
        _grid.Clear();
        _stackGroups.Clear();
        _activeRecords.Clear();
        DestroyPooledObjects();
        UpdateDebugCounters();
    }

    public void ClearOwner(ItemSpawner owner, bool removeGpuInstances)
    {
        if (owner == null) return;

        for (int i = _records.Count - 1; i >= 0; i--)
        {
            VirtualItemBBoxRecord record = _records[i];
            if (record.ownerSpawner != owner) continue;

            ClearRegisteredRecord(record, removeGpuInstances);
        }

        PruneEmptyStackGroups();
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
            GameObject bbox = GetBBoxFromPool();
            ItemBBoxInfo info = bbox.GetComponent<ItemBBoxInfo>();
            BoxCollider boxCollider = bbox.GetComponent<BoxCollider>();
            Renderer renderer = bbox.GetComponent<Renderer>();
            OutlineFx.OutlineFx outlineFx = bbox.GetComponent<OutlineFx.OutlineFx>();
            OutlineController outlineController = bbox.GetComponent<OutlineController>();
            ItemBBoxPhysicsProxy proxy = bbox.GetComponent<ItemBBoxPhysicsProxy>();

            bbox.SetActive(false);
            bbox.name = record.itemId + "_VirtualBBox";
            bbox.tag = "RetailItemBBox";
            bbox.layer = _itemBBoxLayer;
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
            outlineController?.ResetOutlineState();

            info.suppressGpuRemovalOnDestroy = false;
            info.isPhysicsObject = false;
            info.returnToPoolOnDelete = false;
            info.itemId = record.itemId;
            info.expirationDateDecalId = record.expirationDateDecalId;
            info.instanceData = record.instanceData;
            info.physicsSpawnPosition = record.physicsSpawnPosition;
            info.spawnRotation = record.spawnRotation;
            info.PhysicsStack = null;
            info.onBeforeDelete = null;
            info.VirtualRecord = record;

            bool enableShelfPhysics =
                DataHandler.Instance != null && DataHandler.Instance.enableShelfItemPhysics;
            proxy?.ResetForVirtualPoolReuse(enableShelfPhysics);

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
            ItemBBoxPhysicsProxy proxy = bbox.GetComponent<ItemBBoxPhysicsProxy>();
            OutlineController outlineController = bbox.GetComponent<OutlineController>();
            OutlineFx.OutlineFx outlineFx = bbox.GetComponent<OutlineFx.OutlineFx>();
            BoxCollider boxCollider = bbox.GetComponent<BoxCollider>();
            Renderer renderer = bbox.GetComponent<Renderer>();

            proxy?.ResetForVirtualPoolRelease();
            outlineController?.ResetOutlineState();
            if (outlineFx != null) outlineFx.enabled = false;
            if (boxCollider != null) boxCollider.enabled = false;
            if (renderer != null) renderer.enabled = false;

            info.suppressGpuRemovalOnDestroy = true;
            info.isPhysicsObject = false;
            info.returnToPoolOnDelete = false;
            info.itemId = null;
            info.expirationDateDecalId = null;
            info.instanceData = default;
            info.physicsSpawnPosition = default;
            info.spawnRotation = Quaternion.identity;
            info.PhysicsStack = null;
            info.onBeforeDelete = null;
            info.VirtualRecord = null;

            record.activeBBoxInfo = null;
            _activeRecords.Remove(record);

            bbox.name = "PooledItemBBox";
            bbox.transform.SetParent(_poolRoot, worldPositionStays: false);
            bbox.SetActive(false);
            _pool.Push(bbox);
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

        ItemBBoxPhysicsProxy proxy = info.GetComponent<ItemBBoxPhysicsProxy>();
        if (proxy != null && !proxy.CanReturnToVirtualPool)
            return false;

        if (checkStack && info.PhysicsStack != null && !info.PhysicsStack.CanReturnToVirtualPool)
            return false;

        return true;
    }

    private void ClearRegisteredRecord(VirtualItemBBoxRecord record, bool removeGpuInstances)
    {
        if (record == null) return;

        record.consumed = true;
        if (removeGpuInstances)
            RemoveGpuInstance(record);

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
            GameObject bbox = info.gameObject;
            ItemBBoxPhysicsProxy proxy = bbox.GetComponent<ItemBBoxPhysicsProxy>();
            proxy?.ReleasePreviewForVirtualCleanup();

            info.suppressGpuRemovalOnDestroy = true;
            info.PhysicsStack = null;
            info.VirtualRecord = null;
            Destroy(bbox);
        }

        RemoveRecordFromRegistry(record);
    }

    private void RemoveRecordFromRegistry(VirtualItemBBoxRecord record)
    {
        if (record == null) return;

        RemoveFromSpatialIndex(record);
        _records.Remove(record);
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

    private void RemoveGpuInstance(VirtualItemBBoxRecord record)
    {
        if (record == null || GPUInstanceTracker.Instance == null) return;

        BatchInstancer itemBatchInstancer =
            GPUInstanceTracker.Instance.GetBatchInstancerFromId(record.itemId);

        itemBatchInstancer?.RemoveSingleDrawData(record.instanceData);
    }

    private void PruneEmptyStackGroups()
    {
        List<int> emptyGroups = null;
        foreach (KeyValuePair<int, StackGroup> kvp in _stackGroups)
        {
            bool hasLiveRecord = false;
            for (int i = 0; i < kvp.Value.records.Count; i++)
            {
                if (kvp.Value.records[i] != null && !kvp.Value.records[i].consumed)
                {
                    hasLiveRecord = true;
                    break;
                }
            }

            if (hasLiveRecord) continue;

            if (emptyGroups == null)
                emptyGroups = new List<int>();
            emptyGroups.Add(kvp.Key);
        }

        if (emptyGroups == null) return;

        for (int i = 0; i < emptyGroups.Count; i++)
            _stackGroups.Remove(emptyGroups[i]);
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

    private GameObject GetBBoxFromPool()
    {
        EnsureRoots();

        if (_pool.Count > 0)
            return _pool.Pop();

        GameObject bbox = GameObject.CreatePrimitive(PrimitiveType.Cube);
        bbox.name = "PooledItemBBox";
        bbox.AddComponent<ItemBBoxInfo>();
        bbox.AddComponent<OutlineFx.OutlineFx>();
        bbox.AddComponent<OutlineController>();
        bbox.AddComponent<ItemBBoxPhysicsProxy>();
        return bbox;
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
            GameObject pooled = _pool.Pop();
            if (pooled == null) continue;

            ItemBBoxInfo info = pooled.GetComponent<ItemBBoxInfo>();
            if (info != null)
            {
                info.suppressGpuRemovalOnDestroy = true;
                info.VirtualRecord = null;
            }

            Destroy(pooled);
        }
    }

    private void UpdateDebugCounters()
    {
        totalVirtualBBoxes = _records.Count;
        activeRealBBoxes = _activeRecords.Count;
        pooledBBoxes = _pool.Count;
    }
}
