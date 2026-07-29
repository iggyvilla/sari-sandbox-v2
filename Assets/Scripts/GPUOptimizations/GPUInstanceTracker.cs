using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Plane = UnityEngine.Plane;
using Vector3 = UnityEngine.Vector3;

public struct SimplePlane
{
    public float distance;
    public Vector3 normal;
}

// Singleton class made to track BatchInstancer per Item ID
public class GPUInstanceTracker : MonoBehaviour
{
    public static GPUInstanceTracker Instance { get; private set; }
    [SerializeField] private Camera mainCamera;
    public ComputeShader frustumCullingShader;
    [SerializeField] private Shader proceduralUrpLitShader;
    private Dictionary<string, BatchInstancer> trackers = new();
    public SimplePlane[] cameraFrustumPlanes;
    private readonly Plane[] _unityFrustumPlanes = new Plane[6];
    [SerializeField, Min(1f)] private float maxCullingUpdatesPerSecond = 30f;
    [SerializeField, Tooltip("GPU product occlusion quality. Unsupported cameras fall back to frustum culling.")]
    private OcclusionCullingMode occlusionMode = OcclusionCullingMode.Conservative;
    [System.NonSerialized]
    private bool captureOcclusionDebugNextFrame;
    private Matrix4x4 _lastCullingMatrix;
    private bool _hasCullingMatrix;
    private float _nextCullingUpdateTime;
    private bool _forceCullingUpdate = true;

    public uint MainCameraCullingVersion { get; private set; }
    public uint OcclusionStateVersion { get; private set; }

    public OcclusionCullingMode OcclusionMode
    {
        get => occlusionMode;
        set
        {
            if (occlusionMode == value)
                return;

            occlusionMode = value;
            OcclusionStateVersion++;
            RequestCullingUpdate();
        }
    }

    public bool ConsumeOcclusionDebugCaptureRequest()
    {
        if (!captureOcclusionDebugNextFrame)
            return false;

        captureOcclusionDebugNextFrame = false;
        return true;
    }

    [ContextMenu("Capture Occlusion Debug PNGs")]
    private void RequestOcclusionDebugCapture()
    {
        captureOcclusionDebugNextFrame = true;
    }

    // LOD2/LOD3 are disabled until their mesh scales are fixed. Flip to true (here or in
    // the Inspector) to restore all 4 LODs — no other code change needed.
    [SerializeField] private bool enableLod2AndLod3 = false;

    // Default max-distance thresholds (ascending: inner → outer).
    // lods[i].maxDistance = "use this LOD when closer than this distance".
    // lods[0] = LOD0 (closest/highest quality); last entry = hard cull distance (skip entirely beyond this).
    private static readonly float[] DefaultMaxDistances = { 5f, 7f, 10f, 15f };

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    void Update()
    {
        if (mainCamera == null)
        {
            mainCamera = Camera.main;
            if (mainCamera == null) return;

            foreach (BatchInstancer bi in GetComponents<BatchInstancer>())
                bi.agentCamera = mainCamera;
        }

        Matrix4x4 currentCullingMatrix =
            mainCamera.projectionMatrix * mainCamera.worldToCameraMatrix;
        bool matrixChanged =
            !_hasCullingMatrix ||
            !MatricesApproximatelyEqual(currentCullingMatrix, _lastCullingMatrix);
        bool periodicOcclusionUpdate =
            occlusionMode != OcclusionCullingMode.Disabled &&
            GPUOcclusionRendererFeature.IsOcclusionSupported(mainCamera);

        if (!_forceCullingUpdate && !matrixChanged && !periodicOcclusionUpdate)
            return;

        if (!_forceCullingUpdate && Time.unscaledTime < _nextCullingUpdateTime)
            return;

        _lastCullingMatrix = currentCullingMatrix;
        _hasCullingMatrix = true;
        _forceCullingUpdate = false;
        _nextCullingUpdateTime =
            Time.unscaledTime + 1f / Mathf.Max(1f, maxCullingUpdatesPerSecond);
        UpdateFrustumPlanes(mainCamera);
        MainCameraCullingVersion++;
    }

    public BatchInstancer GetBatchInstancerFromId(string itemId)
    {
        if (trackers.ContainsKey(itemId))
            return trackers[itemId];
        return null;
    }

    public Camera MainCamera => mainCamera;

    public void SetCamera(Camera cam)
    {
        if (mainCamera == cam)
            return;

        mainCamera = cam;
        _hasCullingMatrix = false;
        OcclusionStateVersion++;
        RequestCullingUpdate();
        foreach (BatchInstancer bi in GetComponents<BatchInstancer>())
            bi.agentCamera = cam;
    }

    private void RequestCullingUpdate()
    {
        _forceCullingUpdate = true;
        _nextCullingUpdateTime = 0f;
        MainCameraCullingVersion++;
    }

    public void CullForLidarRange(Vector3 origin, float maxRange)
    {
        foreach (BatchInstancer bi in GetComponents<BatchInstancer>())
            bi.CullForLidarRange(origin, maxRange);
    }

    public void AddLidarDrawCommands(CommandBuffer cmd)
    {
        foreach (BatchInstancer bi in GetComponents<BatchInstancer>())
            bi.AddLidarDrawCommands(cmd);
    }

    public LidarIndirectDrawStats AddLidarDepthDrawCommands(CommandBuffer cmd, Material depthMaterial)
    {
        LidarIndirectDrawStats stats = new LidarIndirectDrawStats();

        foreach (BatchInstancer bi in GetComponents<BatchInstancer>())
            stats.Add(bi.AddLidarDepthDrawCommands(cmd, depthMaterial));

        return stats;
    }

    public void DespawnAllItems()
    {
        Debug.Log("Despawning all items...");
        foreach (KeyValuePair<string, BatchInstancer> kvp in trackers)
            kvp.Value.ClearAllDrawData();
    }

    private void UpdateFrustumPlanes(Camera camera)
    {
        GeometryUtility.CalculateFrustumPlanes(camera, _unityFrustumPlanes);
        cameraFrustumPlanes ??= new SimplePlane[6];

        for (int i = 0; i < 6; i++)
        {
            cameraFrustumPlanes[i] = new SimplePlane
            {
                distance = _unityFrustumPlanes[i].distance,
                normal   = _unityFrustumPlanes[i].normal
            };
        }
    }

    private static bool MatricesApproximatelyEqual(Matrix4x4 left, Matrix4x4 right)
    {
        for (int i = 0; i < 16; i++)
        {
            if (Mathf.Abs(left[i] - right[i]) > 0.00001f)
                return false;
        }

        return true;
    }

    public void AddToInstance(string itemId, GameObject obj, InstanceData instanceData)
    {
        if (trackers.ContainsKey(itemId))
        {
            trackers[itemId].AddObjectToBatch(instanceData);
        }
        else
        {
            BatchInstancer batchInstancer = gameObject.AddComponent<BatchInstancer>();
            PrepareBatchInstancer(batchInstancer, obj, itemId);
            batchInstancer.Init();
            batchInstancer.AddObjectToBatch(instanceData);
            trackers[itemId] = batchInstancer;
        }
    }

    /// <summary>True if a combined-chunk batcher already exists for this key.</summary>
    public bool HasChunk(string itemId) => trackers.ContainsKey(itemId);

    /// <summary>
    /// Adds one more instance of an already-registered combined chunk at <paramref name="position"/>.
    /// Used when a later row shares the same (product, arrangement, facing) as an earlier one, so it
    /// can reuse the existing combined mesh instead of rebuilding it.
    /// </summary>
    public void AddChunkInstance(string itemId, Vector3 position)
    {
        if (trackers.TryGetValue(itemId, out BatchInstancer bi))
            bi.AddObjectToBatch(MakeChunkInstanceData(position));
        else
            Debug.LogError($"AddChunkInstance: no chunk registered for '{itemId}'.");
    }

    /// <summary>
    /// Registers a pre-combined row mesh as a single-LOD chunk and draws its first instance.
    ///
    /// Unlike <see cref="AddToInstance"/> (which resolves _LOD0–_LOD3 from a prefab), the mesh here
    /// is a whole combined row. Rows with the same (product, row×stack counts, facing) are identical
    /// relative to their pivot, so they SHARE one key: this builds the batcher + mesh once, and later
    /// rows pile on via <see cref="AddChunkInstance"/>.
    ///
    /// The combined mesh verts are already pivot-relative to <paramref name="position"/> (the row's
    /// first spawn point) with all per-item rotation/scale baked in, so each instance carries identity
    /// rotation and unit scale.
    /// </summary>
    public void AddCombinedChunk(string itemId, Mesh mesh, Material[] materials, Vector3 position)
    {
        InstanceData chunkInstance = MakeChunkInstanceData(position);

        if (trackers.TryGetValue(itemId, out BatchInstancer existing))
        {
            existing.AddObjectToBatch(chunkInstance);
            return;
        }

        BatchInstancer bi = gameObject.AddComponent<BatchInstancer>();
        bi.lods = new[]
        {
            new LODDefinition
            {
                mesh        = mesh,
                materials   = CloneMaterialsForInstancing(materials),
                // Single catch-all LOD: use the original hard-cull distance.
                maxDistance = DefaultMaxDistances[LodHierarchy.MaxLods - 1]
            }
        };
        bi.frustumCullingShader = Instantiate(frustumCullingShader);
        bi.agentCamera          = mainCamera;
        bi.itemId               = itemId;
        bi.Init();
        bi.AddObjectToBatch(chunkInstance);
        trackers[itemId] = bi;
    }

    // A chunk's geometry is baked in world space relative to its pivot, so every LOD slot shares
    // the same identity transform at the pivot position.
    private static InstanceData MakeChunkInstanceData(Vector3 position)
    {
        LodTransform t = new LodTransform
        {
            position = position,
            rotation = new Vector4(0f, 0f, 0f, 1f),
            scale    = Vector3.one
        };
        return new InstanceData { lod0 = t, lod1 = t, lod2 = t, lod3 = t };
    }

    void PrepareBatchInstancer(BatchInstancer bi, GameObject obj, string itemId)
    {
        // Resolve _LOD0–_LOD3 child transforms via the shared resolver (same scan + carry-forward
        // fallback used by ItemSpawner). Missing LODs reuse the last found one, so single-LOD
        // products stay visible through the normal far-distance buckets.
        Transform[] lodTransforms = LodHierarchy.ResolveLodTransforms(obj);

        // LOD2/LOD3 are disabled by default; cap to 2 active LODs until their scales are fixed.
        int activeLods = enableLod2AndLod3 ? LodHierarchy.MaxLods : 2;
        var lodList = new List<LODDefinition>();

        for (int i = 0; i < activeLods; i++)
        {
            Transform t = lodTransforms[i];
            var mf = t.GetComponent<MeshFilter>();
            var mr = t.GetComponent<MeshRenderer>();
            if (mf == null || mr == null) continue;

            // The last active LOD is the catch-all: give it the original hard-cull distance so
            // disabling LOD2/LOD3 preserves the full visibility range instead of shrinking it.
            float maxDistance = (i == activeLods - 1)
                ? DefaultMaxDistances[LodHierarchy.MaxLods - 1]
                : DefaultMaxDistances[i];

            lodList.Add(new LODDefinition
            {
                mesh        = mf.sharedMesh,
                materials   = CloneMaterialsForInstancing(mr.sharedMaterials),
                maxDistance = maxDistance
            });
        }

        if (lodList.Count == 0)
        {
            Debug.LogError("No LOD meshes (_LOD0–_LOD3) found on " + obj.name);
            return;
        }

        bi.lods                 = lodList.ToArray();
        bi.frustumCullingShader = Instantiate(frustumCullingShader);
        bi.agentCamera          = mainCamera;
        bi.itemId               = itemId;
    }

    /* Make each material a new clone of itself, since the
     * custom shader renders materials of the same type at
     * the same time — if the same material is used on different
     * meshes, it freaks out */
    Material[] CloneMaterialsForInstancing(Material[] source)
    {
        Material[] cloned = new Material[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            cloned[i] = new Material(source[i])
            {
                shader            = proceduralUrpLitShader,
                enableInstancing  = true
            };
        }
        return cloned;
    }
}
