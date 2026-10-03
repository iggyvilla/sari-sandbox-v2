using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Side length of the product texture array layers; larger sources are copied from their matching mip.
public enum TextureArrayResolution
{
    [InspectorName("1K")] Res1K = 1024,
    [InspectorName("2K")] Res2K = 2048,
    [InspectorName("4K")] Res4K = 4096
}

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
    [SerializeField] private bool occlusionCulling = false;

    [Tooltip("Scene view draws the main camera's culling results instead of culling itself, so culling is visible.")]
    [SerializeField] private bool sceneViewShowsMainCameraCulling = true;

    [Tooltip("Merge opaque submeshes with matching render state into one draw (see SubmeshMerger).")]
    [SerializeField] private bool mergeSubmeshes = true;

    // Defaults are the Balanced preset; saved settings and command-line flags override them (see RenderingSettings).
    [Tooltip("Sample opaque product albedo from packed texture arrays so products share one material (see ProductTextureAtlas).")]
    [SerializeField] private bool useTextureArrays = true;

    [Tooltip("Layer size of the texture arrays (memory vs. sharpness). Products with smaller textures keep their size.")]
    [SerializeField] private TextureArrayResolution textureArrayResolution = TextureArrayResolution.Res2K;

    [Tooltip("Draw with Graphics.RenderMeshIndirect: shared draw buffers, no per-draw property blocks, and identical " +
             "materials shared across products, so the renderer changes state far less (see InstanceCullingSystem).")]
    [SerializeField] private bool useIndirectArgs = true;

    // LOD2/LOD3 are disabled until their mesh scales are fixed.
    [SerializeField] private bool enableLod2AndLod3 = false;

    // Ascending max distances; the last is the hard cull distance.
    private static readonly float[] DefaultMaxDistances = { 5f, 7f, 10f, 15f };
    private const int DefaultActiveLods = 2;
    private const int DestroyedCameraPurgeInterval = 120;
    private const string FrustumCullingFlag = "-sariFrustumCulling";
    private const string OcclusionCullingFlag = "-sariOcclusionCulling";
    private const string TextureArraysFlag = "-sariTextureArrays";
    private const string TextureResFlag = "-sariTextureRes";
    private const string IndirectArgsFlag = "-sariIndirectArgs";

    // What a LOD was built from, so batchers can be rebuilt when texture arrays are toggled.
    private readonly struct LodSource
    {
        public readonly Mesh mesh;
        public readonly Material[] materials;
        public readonly float maxDistance;

        public LodSource(Mesh mesh, Material[] materials, float maxDistance)
        {
            this.mesh = mesh;
            this.materials = materials;
            this.maxDistance = maxDistance;
        }
    }

    private readonly Dictionary<string, BatchInstancer> _batchers = new();
    private readonly Dictionary<string, LodSource[]> _lodSources = new();
    private readonly List<BatchInstancer> _pending = new();
    private ProductTextureAtlas _atlas;
    private SharedMaterialPool _sharedMaterials;
    private readonly Plane[] _unityPlanes = new Plane[6];
    private readonly Vector4[] _planes = new Vector4[6];
    private CommandBuffer _cullCommands;
    private InstanceCullingSystem _culling;

    public Camera MainCamera => mainCamera;
    public bool UseTextureArrays => useTextureArrays;
    public bool UseIndirectArgs => useIndirectArgs;
    public TextureArrayResolution TextureResolution => textureArrayResolution;
    public ProductTextureAtlas TextureAtlas => _atlas;
    public RenderingSettings.Options RenderingOptions =>
        new(useTextureArrays, useIndirectArgs, textureArrayResolution, RenderingSettings.FullResSsao);

    // How many LODs instanced products use (physics prefabs mirror this, see ProductLodSetup).
    public static int ActiveLodCount =>
        Instance != null && Instance.enableLod2AndLod3 ? LodHierarchy.MaxLods : DefaultActiveLods;

    // Distance below which LOD `lod` is used.
    public static float LodMaxDistance(int lod) => DefaultMaxDistances[lod];

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
        RenderingSettings.Options rendering = RenderingSettings.Startup(RenderingOptions);
        useTextureArrays = ReadToggleArgument(TextureArraysFlag, rendering.textureArrays);
        textureArrayResolution = ReadResolutionArgument(TextureResFlag, rendering.resolution);
        useIndirectArgs = ReadToggleArgument(IndirectArgsFlag, rendering.indirectArgs);
        _culling.IndirectArgs = useIndirectArgs;
        RenderingSettings.FullResSsao = rendering.fullResSsao;
    }

    // "-flag off" / "-flag on" overrides the Inspector value in player builds.
    private static bool ReadToggleArgument(string flag, bool fallback)
    {
        string value = CommandLineArgs.Get(flag);
        if (string.IsNullOrEmpty(value)) return fallback;
        return !value.Equals("off", System.StringComparison.OrdinalIgnoreCase) && value != "0" &&
               !value.Equals("false", System.StringComparison.OrdinalIgnoreCase);
    }

    // "-flag 1024|2048|4096" overrides the Inspector value in player builds.
    private static TextureArrayResolution ReadResolutionArgument(string flag, TextureArrayResolution fallback)
    {
        string value = CommandLineArgs.Get(flag);
        if (string.IsNullOrEmpty(value)) return fallback;
        if (int.TryParse(value, out int size) && Enum.IsDefined(typeof(TextureArrayResolution), size))
            return (TextureArrayResolution)size;
        Debug.LogWarning($"{flag}: '{value}' is not 1024, 2048 or 4096; using {(int)fallback}.");
        return fallback;
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
        _atlas?.Dispose();
        _sharedMaterials?.Dispose();
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
        FlushPending();
        if (cam.cameraType == CameraType.SceneView && sceneViewShowsMainCameraCulling &&
            _culling.RenderCameraAs(cam, mainCamera != null ? mainCamera : Camera.main))
            return;

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
        FlushPending();
        _culling.CullForLidarRange(_cullCommands, origin, maxRange);
        ExecuteCullCommands();
    }

    public LidarIndirectDrawStats AddLidarDepthDrawCommands(CommandBuffer cmd, Material depthMaterial)
    {
        FlushPending();
        return _culling.AddLidarDepthDrawCommands(cmd, depthMaterial);
    }

    public void DespawnAllItems()
    {
        foreach (BatchInstancer bi in _batchers.Values)
            bi.ClearAllDrawData();
    }

    public void AddToInstance(string itemId, GameObject obj, InstanceData instanceData)
    {
        if (!_batchers.TryGetValue(itemId, out BatchInstancer bi))
        {
            LodSource[] sources = ResolveLodSources(obj);
            if (sources == null) return;
            bi = CreateBatcher(itemId, sources);
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
                new LodSource(mesh, materials, DefaultMaxDistances[LodHierarchy.MaxLods - 1])
            });
        }
        bi.AddObjectToBatch(MakeChunkInstanceData(position));
    }

    private BatchInstancer CreateBatcher(string itemId, LodSource[] sources)
    {
        BatchInstancer bi = gameObject.AddComponent<BatchInstancer>();
        bi.itemId = itemId;
        _lodSources[itemId] = sources;
        _batchers[itemId] = bi;
        // With texture arrays the LODs wait for FlushPending, so every product spawned together is packed together.
        if (useTextureArrays) _pending.Add(bi);
        else Activate(bi);
        return bi;
    }

    private void Activate(BatchInstancer bi)
    {
        bi.Init(bi.itemId, BuildLodDefinitions(_lodSources[bi.itemId]));
        _culling.Add(bi);
    }

    // Packs the textures of every batcher created since the last flush in one go (dense arrays, only products that
    // are really in the store), then gives them their LODs. Runs before anything draws or culls.
    private void FlushPending()
    {
        if (_pending.Count == 0) return;
        if (useTextureArrays) PackSources(_pending.ConvertAll(b => _lodSources[b.itemId]));
        foreach (BatchInstancer bi in _pending) Activate(bi);
        _pending.Clear();
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

    private LodSource[] ResolveLodSources(GameObject obj)
    {
        Transform[] lodTransforms = LodHierarchy.ResolveLodTransforms(obj);
        int activeLods = ActiveLodCount;
        var lodList = new List<LodSource>();

        for (int i = 0; i < activeLods; i++)
        {
            Transform t = lodTransforms[i];
            if (!t.TryGetComponent(out MeshFilter mf) || !t.TryGetComponent(out MeshRenderer mr)) continue;

            // The last active LOD keeps the full hard-cull distance so disabling LOD2/3 doesn't shrink range.
            float maxDistance = i == activeLods - 1
                ? DefaultMaxDistances[LodHierarchy.MaxLods - 1]
                : DefaultMaxDistances[i];

            lodList.Add(new LodSource(mf.sharedMesh, mr.sharedMaterials, maxDistance));
        }

        if (lodList.Count > 0) return lodList.ToArray();

        Debug.LogError("No LOD meshes (_LOD0–_LOD3) found on " + obj.name);
        return null;
    }

    private LODDefinition[] BuildLodDefinitions(LodSource[] sources)
    {
        ProductTextureAtlas atlas = useTextureArrays ? _atlas : null; // packed by FlushPending / RebuildLods
        SharedMaterialPool pool = null;
        if (useIndirectArgs) pool = _sharedMaterials ??= new SharedMaterialPool();
        var definitions = new LODDefinition[sources.Length];
        for (int i = 0; i < sources.Length; i++)
        {
            LodSource src = sources[i];
            definitions[i] = BuildLod(
                src.mesh, src.materials, src.maxDistance, proceduralUrpLitShader, mergeSubmeshes, atlas, pool);
        }
        return definitions;
    }

    // Switches between per-product materials and texture arrays, rebuilding every batcher's LODs.
    public void SetUseTextureArrays(bool enabled)
    {
        if (useTextureArrays == enabled) return;
        useTextureArrays = enabled;
        RebuildLods();
    }

    // Switches between per-draw property blocks and shared draw buffers (with shared materials).
    public void SetUseIndirectArgs(bool enabled)
    {
        if (useIndirectArgs == enabled) return;
        useIndirectArgs = enabled;
        _culling.IndirectArgs = enabled;
        RebuildLods();
    }

    // Switches the layer size, repacking every product that has a batcher.
    public void SetTextureArrayResolution(TextureArrayResolution resolution)
    {
        if (textureArrayResolution == resolution) return;
        textureArrayResolution = resolution;
        if (!useTextureArrays) return; // nothing is packed; the next pack uses the new size
        ProductTextureAtlas old = _atlas;
        _atlas = null;
        RebuildLods();
        old?.Dispose();
    }

    // Fresh shared materials per rebuild, so the old ones (and the arrays they sample) are freed once unused.
    private void RebuildLods()
    {
        FlushPending();
        if (useTextureArrays) PackSources(_lodSources.Values);
        SharedMaterialPool oldPool = _sharedMaterials;
        _sharedMaterials = null;
        foreach (KeyValuePair<string, BatchInstancer> entry in _batchers)
            entry.Value.SetLods(BuildLodDefinitions(_lodSources[entry.Key]));
        oldPool?.Dispose();
        if (useTextureArrays) return;
        _atlas?.Dispose();
        _atlas = null;
    }

    // Adds the textures of these LODs the atlas doesn't have yet: free space first, then a new array.
    private void PackSources(IEnumerable<LodSource[]> sources)
    {
        var needs = new Dictionary<Texture2D, ProductTextureAtlas.Wrap>();
        foreach (LodSource[] lods in sources)
            foreach (LodSource lod in lods)
                ProductTextureAtlas.CollectNeeds(lod.mesh, lod.materials, needs);
        _atlas ??= new ProductTextureAtlas((int)textureArrayResolution);
        int added = _atlas.Add(needs);
        if (added > 0)
        {
            Debug.Log($"GPUInstanceTracker: packed {added} textures; atlas has {_atlas.TextureCount} textures in " +
                      $"{_atlas.LayerCount} layers of {_atlas.LayerSize}px across {_atlas.Arrays.Count} arrays " +
                      $"({_atlas.MemoryBytes / 1048576} MB, {_atlas.Efficiency:P1} used, {_atlas.BuildMilliseconds:F0} ms total).");
        }
    }

    // Shared with editor tools so previews use the exact runtime mesh/material conversion. Without a pool every
    // material is a clone this LOD owns (see LODDefinition.owned); with one, equal clones are shared across LODs.
    public static LODDefinition BuildLod(
        Mesh mesh, Material[] sourceMaterials, float maxDistance, Shader instancingShader, bool merge,
        ProductTextureAtlas atlas = null, SharedMaterialPool pool = null)
    {
        Mesh sourceMesh = mesh;
        var clones = new List<Material>();
        Material Clone(Material m)
        {
            Material clone = CloneForInstancing(m, instancingShader);
            clones.Add(clone);
            return clone;
        }
        Material Share(Material m) => pool != null ? pool.Intern(m) : m;

        Material[] materials;
        if (merge && atlas != null)
            (mesh, materials) = SubmeshMerger.MergeIntoArrays(mesh, sourceMaterials, atlas, instancingShader, Clone, Share);
        else
        {
            materials = Array.ConvertAll(sourceMaterials, Clone);
            if (merge)
                (mesh, materials) = SubmeshMerger.Merge(mesh, materials);
            materials = Array.ConvertAll(materials, Share);
        }

        // Clones the merge left unused are freed now; used ones belong to the pool or to this LOD.
        var owned = new List<UnityEngine.Object>();
        if (mesh != sourceMesh) owned.Add(mesh);
        foreach (Material clone in clones)
        {
            if (clone == null) continue;
            if (Array.IndexOf(materials, clone) < 0) DestroyOwned(clone);
            else if (pool == null) owned.Add(clone);
        }
        return new LODDefinition { mesh = mesh, materials = materials, maxDistance = maxDistance, owned = owned.ToArray() };
    }

    private static void DestroyOwned(UnityEngine.Object o)
    {
        if (Application.isPlaying) Destroy(o);
        else DestroyImmediate(o);
    }

    // Per-product clones (one per batcher) keep procedural data per material for the property-block draws.
    // Array materials hold no per-product data (everything per-part is vertex data), and with shared draw buffers
    // neither do the others, so a pool can serve every product.
    private static Material CloneForInstancing(Material source, Shader instancingShader)
    {
        var clone = new Material(source)
        {
            shader = instancingShader,
            enableInstancing = true
        };
        ProductMaterials.ApplyDepthWrite(clone);
        return clone;
    }
}
