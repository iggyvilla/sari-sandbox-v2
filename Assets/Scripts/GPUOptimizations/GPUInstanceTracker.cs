using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Singleton that owns one BatchInstancer per item ID and drives per-camera GPU culling + drawing.
public class GPUInstanceTracker : MonoBehaviour
{
    public static GPUInstanceTracker Instance { get; private set; }

    [SerializeField] private Camera mainCamera;
    public ComputeShader frustumCullingShader;
    [SerializeField] private Shader proceduralUrpLitShader;

    [Tooltip("Cull GPU-instanced products outside each camera's frustum. Off = draw everything within LOD range.")]
    [SerializeField] private bool frustumCulling = true;

    [Tooltip("Also cull products hidden behind this frame's depth prepass (needs HiZOcclusionFeature on the renderer).")]
    [SerializeField] private bool occlusionCulling = true;

    // LOD2/LOD3 are disabled until their mesh scales are fixed.
    [SerializeField] private bool enableLod2AndLod3 = false;

    // Ascending max distances; the last is the hard cull distance.
    private static readonly float[] DefaultMaxDistances = { 5f, 7f, 10f, 15f };
    private const int DestroyedCameraPurgeInterval = 120;
    private const string FrustumCullingFlag = "-sariFrustumCulling";
    private const string OcclusionCullingFlag = "-sariOcclusionCulling";

    private readonly Dictionary<string, BatchInstancer> _batchers = new();
    private readonly Plane[] _unityPlanes = new Plane[6];
    private readonly Vector4[] _planes = new Vector4[6];
    private CommandBuffer _cullCommands;
    private InstanceCullingSystem _culling;

    public Camera MainCamera => mainCamera;

    public bool FrustumCullingEnabled
    {
        get => frustumCulling;
        set => frustumCulling = value;
    }

    public bool OcclusionCullingEnabled
    {
        get => occlusionCulling;
        set => occlusionCulling = value;
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        _culling = new InstanceCullingSystem(frustumCullingShader);
        frustumCulling = ReadToggleArgument(FrustumCullingFlag, frustumCulling);
        occlusionCulling = ReadToggleArgument(OcclusionCullingFlag, occlusionCulling);
    }

    // "-flag off" / "-flag on" overrides the Inspector value in player builds.
    private static bool ReadToggleArgument(string flag, bool fallback)
    {
        string value = CommandLineArgs.Get(flag);
        if (string.IsNullOrEmpty(value)) return fallback;
        return !value.Equals("off", System.StringComparison.OrdinalIgnoreCase) && value != "0" &&
               !value.Equals("false", System.StringComparison.OrdinalIgnoreCase);
    }

    void OnEnable()
    {
        _cullCommands ??= new CommandBuffer { name = "GPU Instance Culling" };
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
    }

    void OnDisable() => RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;

    void OnDestroy()
    {
        _cullCommands?.Release();
        _culling?.Dispose();
        if (Instance == this) Instance = null;
    }

    // Cameras that draw products; occlusion only applies to these.
    public static bool DrawsProducts(Camera cam) =>
        (cam.cameraType == CameraType.Game || cam.cameraType == CameraType.SceneView) && (cam.cullingMask & 1) != 0;

    void LateUpdate()
    {
        if (Time.frameCount % DestroyedCameraPurgeInterval == 0)
            _culling.ReleaseDestroyedCameras();
    }

    private void OnBeginCameraRendering(ScriptableRenderContext _, Camera cam)
    {
        if (!DrawsProducts(cam)) return;

        CullView view = CullView.ForCamera(
            cam.transform.position,
            frustumCulling ? CalculatePlanes(cam) : null,
            occlusionCulling && cam.cameraType == CameraType.Game);
        _culling.RenderCamera(_cullCommands, cam, view);
        ExecuteCullCommands();
    }

    // Called by HiZOcclusionFeature after the depth prepass, before products draw in the opaque pass.
    public void RecordOcclusionCull(CommandBuffer cmd, Camera cam, OcclusionView occlusion)
    {
        _culling.RecordOcclusionCull(cmd, cam, occlusion);
    }

    // Debug: total instances drawn for a camera on its last render.
    public int ReadVisibleCount(Camera cam) => _culling.ReadVisibleCount(cam);

    private void ExecuteCullCommands()
    {
        Graphics.ExecuteCommandBuffer(_cullCommands);
        _cullCommands.Clear();
    }

    private Vector4[] CalculatePlanes(Camera cam)
    {
        GeometryUtility.CalculateFrustumPlanes(cam, _unityPlanes);
        for (int i = 0; i < _planes.Length; i++)
        {
            Plane p = _unityPlanes[i];
            _planes[i] = new Vector4(p.normal.x, p.normal.y, p.normal.z, p.distance);
        }
        return _planes;
    }

    public BatchInstancer GetBatchInstancerFromId(string itemId) =>
        _batchers.TryGetValue(itemId, out BatchInstancer bi) ? bi : null;

    public void SetCamera(Camera cam) => mainCamera = cam;

    public void CullForLidarRange(Vector3 origin, float maxRange)
    {
        _culling.CullForLidarRange(_cullCommands, origin, maxRange);
        ExecuteCullCommands();
    }

    public LidarIndirectDrawStats AddLidarDepthDrawCommands(CommandBuffer cmd, Material depthMaterial) =>
        _culling.AddLidarDepthDrawCommands(cmd, depthMaterial);

    public void DespawnAllItems()
    {
        foreach (BatchInstancer bi in _batchers.Values)
            bi.ClearAllDrawData();
    }

    public void AddToInstance(string itemId, GameObject obj, InstanceData instanceData)
    {
        if (!_batchers.TryGetValue(itemId, out BatchInstancer bi))
        {
            LODDefinition[] lodDefinitions = BuildLodDefinitions(obj);
            if (lodDefinitions == null) return;
            bi = CreateBatcher(itemId, lodDefinitions);
        }
        bi.AddObjectToBatch(instanceData);
    }

    /// <summary>True if a combined-chunk batcher already exists for this key.</summary>
    public bool HasChunk(string itemId) => _batchers.ContainsKey(itemId);

    /// <summary>Adds another instance of an already-registered combined chunk.</summary>
    public void AddChunkInstance(string itemId, Vector3 position)
    {
        if (_batchers.TryGetValue(itemId, out BatchInstancer bi))
            bi.AddObjectToBatch(MakeChunkInstanceData(position));
        else
            Debug.LogError($"AddChunkInstance: no chunk registered for '{itemId}'.");
    }

    /// <summary>
    /// Registers a pre-combined row mesh (verts pivot-relative, rotation/scale baked in) as a single-LOD
    /// chunk and draws its first instance. Rows with the same arrangement share one key.
    /// </summary>
    public void AddCombinedChunk(string itemId, Mesh mesh, Material[] materials, Vector3 position)
    {
        if (!_batchers.TryGetValue(itemId, out BatchInstancer bi))
        {
            bi = CreateBatcher(itemId, new[]
            {
                new LODDefinition
                {
                    mesh = mesh,
                    materials = CloneMaterialsForInstancing(materials),
                    maxDistance = DefaultMaxDistances[LodHierarchy.MaxLods - 1]
                }
            });
        }
        bi.AddObjectToBatch(MakeChunkInstanceData(position));
    }

    private BatchInstancer CreateBatcher(string itemId, LODDefinition[] lodDefinitions)
    {
        BatchInstancer bi = gameObject.AddComponent<BatchInstancer>();
        bi.Init(itemId, lodDefinitions);
        _batchers[itemId] = bi;
        _culling.Add(bi);
        return bi;
    }

    // A chunk's geometry is baked relative to its pivot, so every LOD slot shares one identity transform.
    private static InstanceData MakeChunkInstanceData(Vector3 position)
    {
        LodTransform t = new LodTransform
        {
            position = position,
            rotation = new Vector4(0f, 0f, 0f, 1f),
            scale = Vector3.one
        };
        return new InstanceData { lod0 = t, lod1 = t, lod2 = t, lod3 = t };
    }

    private LODDefinition[] BuildLodDefinitions(GameObject obj)
    {
        Transform[] lodTransforms = LodHierarchy.ResolveLodTransforms(obj);
        int activeLods = enableLod2AndLod3 ? LodHierarchy.MaxLods : 2;
        var lodList = new List<LODDefinition>();

        for (int i = 0; i < activeLods; i++)
        {
            Transform t = lodTransforms[i];
            if (!t.TryGetComponent(out MeshFilter mf) || !t.TryGetComponent(out MeshRenderer mr)) continue;

            // The last active LOD keeps the full hard-cull distance so disabling LOD2/3 doesn't shrink range.
            float maxDistance = i == activeLods - 1
                ? DefaultMaxDistances[LodHierarchy.MaxLods - 1]
                : DefaultMaxDistances[i];

            lodList.Add(new LODDefinition
            {
                mesh = mf.sharedMesh,
                materials = CloneMaterialsForInstancing(mr.sharedMaterials),
                maxDistance = maxDistance
            });
        }

        if (lodList.Count > 0) return lodList.ToArray();

        Debug.LogError("No LOD meshes (_LOD0–_LOD3) found on " + obj.name);
        return null;
    }

    // Each batcher needs its own material clones: shared materials across meshes break procedural instancing.
    private Material[] CloneMaterialsForInstancing(Material[] source)
    {
        var cloned = new Material[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            cloned[i] = new Material(source[i])
            {
                shader = proceduralUrpLitShader,
                enableInstancing = true
            };
        }
        return cloned;
    }
}
