using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

// Per-LOD transform uploaded to the GPU.
[StructLayout(LayoutKind.Sequential)]
public struct LodTransform : IEquatable<LodTransform>
{
    public Vector3 position;
    public Vector4 rotation;
    public Vector3 scale;

    public bool Equals(LodTransform other) =>
        position == other.position && rotation == other.rotation && scale == other.scale;
}

// CPU-side source data: one independent transform per LOD level.
// lod0 is always present; missing LODs carry the last found one (see LodHierarchy).
[StructLayout(LayoutKind.Sequential)]
public struct InstanceData : IEquatable<InstanceData>
{
    public LodTransform lod0;
    public LodTransform lod1;
    public LodTransform lod2;
    public LodTransform lod3;

    public LodTransform this[int lod] => lod switch
    {
        1 => lod1,
        2 => lod2,
        3 => lod3,
        _ => lod0
    };

    public bool Equals(InstanceData other) =>
        lod0.Equals(other.lod0) && lod1.Equals(other.lod1) &&
        lod2.Equals(other.lod2) && lod3.Equals(other.lod3);

    public override int GetHashCode() =>
        HashCode.Combine(lod0.position, lod0.rotation, lod0.scale,
                         lod1.position, lod1.rotation, lod1.scale);
}

// Matches the HLSL LodRenderData struct in shaderGraphSupport.hlsl.
[StructLayout(LayoutKind.Sequential)]
public struct LodRenderData
{
    public Vector4 rotation;
    public Vector4 scale;
}

// One LOD level: mesh, instancing materials, and the distance below which it's used.
// maxDistance of the last entry is the hard cull distance.
[Serializable]
public struct LODDefinition
{
    public Mesh mesh;
    public Material[] materials;
    [Tooltip("Use this LOD when closer than this distance. The last (farthest) LOD's value is the hard cull distance.")]
    public float maxDistance;
}

public struct LidarIndirectDrawStats
{
    public int batchers;
    public int readyBatchers;
    public int skippedNotReady;
    public int skippedNoInstances;
    public int skippedMissingBuffers;
    public int sourceInstances;
    public int lods;
    public int submeshes;
    public int queuedCommands;

    public void Add(LidarIndirectDrawStats other)
    {
        batchers += other.batchers;
        readyBatchers += other.readyBatchers;
        skippedNotReady += other.skippedNotReady;
        skippedNoInstances += other.skippedNoInstances;
        skippedMissingBuffers += other.skippedMissingBuffers;
        sourceInstances += other.sourceInstances;
        lods += other.lods;
        submeshes += other.submeshes;
        queuedCommands += other.queuedCommands;
    }
}

// What to cull against: a camera (optional frustum + LOD hard-cull distance) or a LiDAR range sphere.
public readonly struct CullView
{
    public readonly Vector4[] planes; // null = no frustum test
    public readonly Vector3 origin;
    public readonly float rangeSq;    // > 0 = LiDAR range mode
    public readonly bool occlusion;   // an occlusion pass will re-cull these results this frame

    private CullView(Vector4[] planes, Vector3 origin, float rangeSq, bool occlusion = false)
    {
        this.occlusion = occlusion;
        this.planes = planes;
        this.origin = origin;
        this.rangeSq = rangeSq;
    }

    public bool IsRange => rangeSq > 0f;

    public static CullView ForCamera(Vector3 origin, Vector4[] frustumPlanes, bool occlusion) =>
        new(frustumPlanes, origin, 0f, occlusion);

    public static CullView ForRange(Vector3 origin, float range) =>
        new(null, origin, Mathf.Max(0.01f, range) * Mathf.Max(0.01f, range));
}

// This frame's Hi-Z pyramid for one camera (see HiZOcclusionFeature).
public sealed class OcclusionView
{
    public Texture hiz;
    public Matrix4x4 worldToView;
    public Vector4 projection; // proj[0][0], proj[1][1], near, depth bias
    public Vector4 size;       // width, height, mip count
}

// GPU-instanced renderer for every instance of one product (or combined row chunk).
// GPUInstanceTracker drives it per camera: cull on the GPU into per-LOD visible lists, then draw indirect.
public class BatchInstancer : MonoBehaviour
{
    public const int MaxLods = 4;

    // Keep in sync with FrustumCullingFilterer.compute.
    private const int CullFrustum = 1;
    private const int CullRange = 2;
    private const int CullOcclusion = 4;

    // CPU mirror of the GPU test is made slightly more permissive so it never drops a LOD the GPU fills.
    private const float CpuCullEpsilon = 1e-3f;

    private const int ArgsStride = 5;
    private const int AllLodsMask = (1 << MaxLods) - 1;

    private static readonly ProfilerMarker CullMarker = new("Frustum Culling");
    private static readonly string[] LodBufNames = { "lod0_buf", "lod1_buf", "lod2_buf", "lod3_buf" };
    private static readonly int[] LodBufIds = Array.ConvertAll(LodBufNames, Shader.PropertyToID);
    private static readonly int PositionBufferId = Shader.PropertyToID("position_buffer");
    private static readonly int SphereBufferId = Shader.PropertyToID("sphere_buffer");
    private static readonly int NumToDrawId = Shader.PropertyToID("num_to_draw");
    private static readonly int PlanesId = Shader.PropertyToID("planes");
    private static readonly int CullFlagsId = Shader.PropertyToID("cull_flags");
    private static readonly int CullOriginId = Shader.PropertyToID("cull_origin");
    private static readonly int RangeMaxDistanceSqId = Shader.PropertyToID("range_max_distance_sq");
    private static readonly int LodDistancesId = Shader.PropertyToID("lod_distances");
    private static readonly int NumLodsId = Shader.PropertyToID("num_lods");
    private static readonly int VisibleIndicesId = Shader.PropertyToID("_VisibleIndices");
    private static readonly int PositionsId = Shader.PropertyToID("_Positions");
    private static readonly int LodTransformDataId = Shader.PropertyToID("_LodTransformData");
    private static readonly int HiZTextureId = Shader.PropertyToID("hiz_texture");
    private static readonly int HiZWorldToViewId = Shader.PropertyToID("hiz_world_to_view");
    private static readonly int HiZProjectionId = Shader.PropertyToID("hiz_projection");
    private static readonly int HiZSizeId = Shader.PropertyToID("hiz_size");

    // GPU results for one viewer (a camera or the LiDAR). Separate per viewer so cameras never share counts.
    private sealed class CullResult
    {
        public ComputeBuffer[] visible;
        public ComputeBuffer args;
        public MaterialPropertyBlock[] props;
        public int capacity;
        public int boundVersion = -1;
        public bool hasResults;
        public int flags;
        public Matrix4x4 viewKey;
        public Vector3 originKey;
        public float rangeKey;
        public int lodMask;
        public int drawnFrame = -1;
        public readonly Vector4[] planes = new Vector4[6];

        public void Release()
        {
            if (visible != null)
                foreach (ComputeBuffer buffer in visible) buffer?.Release();
            args?.Release();
            visible = null;
            args = null;
        }
    }

    public LODDefinition[] lods;
    public string itemId;

    private ComputeShader _cullingShader;
    private int _kernel;
    private uint _threadGroupSize;

    private readonly List<InstanceData> _instances = new();
    private Vector4[] _spheres = Array.Empty<Vector4>();
    private ComputeBuffer _positionBuffer;
    private ComputeBuffer _sphereBuffer;
    private ComputeBuffer[] _lodTransformBuffers;
    private ComputeBuffer _dummyAppendBuffer;

    private int[] _argsEntryStart;
    private int[] _subMeshCounts;
    private uint[] _argsTemplate;
    private int _argsEntryCount;
    private Vector4 _lodDistancesSq;

    private readonly Dictionary<Camera, CullResult> _cameraResults = new();
    private static readonly List<Camera> DestroyedCameras = new();
    private CullResult _lidarResult;

    private bool _ready;
    private bool _buffersDirty;
    private int _dataVersion;
    private Bounds _cullingBounds;
    private Bounds _drawBounds;

    public int InstanceCount => _instances.Count;
    public int IndirectDrawCommandCount => _argsEntryCount;
    private float HardCullDistanceSq => _lodDistancesSq[lods.Length - 1];

    public void Init(string id, LODDefinition[] lodDefinitions, ComputeShader cullingShader)
    {
        itemId = id;
        lods = lodDefinitions;
        _cullingShader = cullingShader;

        if (lods == null || lods.Length == 0 || lods.Length > MaxLods)
        {
            Debug.LogError($"BatchInstancer ({itemId}): expected 1-{MaxLods} LODs.");
            return;
        }

        _kernel = _cullingShader.FindKernel("CSMain");
        _cullingShader.GetKernelThreadGroupSizes(_kernel, out _threadGroupSize, out _, out _);

        _argsEntryStart = new int[lods.Length];
        _subMeshCounts = new int[lods.Length];
        var template = new List<uint>();
        for (int lod = 0; lod < lods.Length; lod++)
        {
            Mesh mesh = lods[lod].mesh;
            Material[] materials = lods[lod].materials;
            _argsEntryStart[lod] = _argsEntryCount;
            _subMeshCounts[lod] = mesh != null && materials != null
                ? Mathf.Min(materials.Length, mesh.subMeshCount)
                : 0;

            for (int s = 0; s < _subMeshCounts[lod]; s++)
            {
                // index count, instance count (filled by CopyCount), start index, base vertex, start instance
                template.Add(mesh.GetIndexCount(s));
                template.Add(0);
                template.Add(mesh.GetIndexStart(s));
                template.Add(mesh.GetBaseVertex(s));
                template.Add(0);
            }

            _argsEntryCount += _subMeshCounts[lod];
            _lodDistancesSq[lod] = lods[lod].maxDistance * lods[lod].maxDistance;
        }

        _argsTemplate = template.ToArray();
        _dummyAppendBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Append);
        _ready = true;
    }

    void OnDestroy()
    {
        foreach (CullResult result in _cameraResults.Values) result.Release();
        _cameraResults.Clear();
        _lidarResult?.Release();
        ReleaseInstanceBuffers();
        _dummyAppendBuffer?.Release();
    }

    public void AddObjectToBatch(InstanceData d)
    {
        _instances.Add(d);
        _buffersDirty = true;
    }

    public void RemoveSingleDrawData(InstanceData d)
    {
        if (_instances.Remove(d))
            _buffersDirty = true;
    }

    // Bulk removal: one pass instead of a linear search per instance.
    public void RemoveDrawData(HashSet<InstanceData> toRemove)
    {
        if (_instances.RemoveAll(toRemove.Contains) > 0)
            _buffersDirty = true;
    }

    public void ClearAllDrawData()
    {
        _instances.Clear();
        _buffersDirty = true;
    }

    public void RenderForCamera(CommandBuffer cmd, Camera cam, in CullView view)
    {
        if (!PrepareBuffers() || !BoundsVisible(view)) return;

        if (!_cameraResults.TryGetValue(cam, out CullResult result))
            _cameraResults[cam] = result = new CullResult();

        Cull(cmd, result, view, cam.cullingMatrix);
        result.drawnFrame = Time.frameCount;

        for (int lod = 0; lod < lods.Length; lod++)
        {
            if ((result.lodMask & (1 << lod)) == 0) continue;
            for (int s = 0; s < _subMeshCounts[lod]; s++)
            {
                Graphics.DrawMeshInstancedIndirect(
                    lods[lod].mesh,
                    s,
                    lods[lod].materials[s],
                    _drawBounds,
                    result.args,
                    ArgsOffset(lod, s),
                    result.props[lod],
                    ShadowCastingMode.Off,
                    true,
                    0,
                    cam);
            }
        }
    }

    // Re-culls this camera's frustum results against its Hi-Z so the opaque pass skips hidden instances.
    public void RecordOcclusionCull(CommandBuffer cmd, Camera cam, OcclusionView occlusion)
    {
        if (!_cameraResults.TryGetValue(cam, out CullResult result) ||
            !result.hasResults || result.lodMask == 0 || result.boundVersion != _dataVersion)
            return;

        bool frustum = (result.flags & CullFrustum) != 0;
        RecordDispatch(
            cmd, result, (result.flags & ~CullRange) | CullOcclusion, frustum ? result.planes : null,
            result.originKey, 0f, occlusion);
    }

    // Debug: instances this camera's last draw used (synchronous GPU readback; don't call per frame).
    public int ReadVisibleCount(Camera cam)
    {
        if (!_cameraResults.TryGetValue(cam, out CullResult result) || !result.hasResults ||
            _argsEntryCount == 0 || result.drawnFrame < Time.frameCount - 1)
            return 0;

        var args = new uint[_argsEntryCount * ArgsStride];
        result.args.GetData(args);
        int total = 0;
        for (int lod = 0; lod < lods.Length; lod++)
            if ((result.lodMask & (1 << lod)) != 0 && _subMeshCounts[lod] > 0)
                total += (int)args[_argsEntryStart[lod] * ArgsStride + 1];
        return total;
    }

    public void ReleaseDestroyedCameraResults()
    {
        foreach (Camera cam in _cameraResults.Keys)
            if (cam == null) DestroyedCameras.Add(cam);

        foreach (Camera cam in DestroyedCameras)
        {
            _cameraResults[cam].Release();
            _cameraResults.Remove(cam);
        }
        DestroyedCameras.Clear();
    }

    public void CullForLidarRange(CommandBuffer cmd, Vector3 origin, float maxRange)
    {
        if (!PrepareBuffers()) return;

        _lidarResult ??= new CullResult();
        Cull(cmd, _lidarResult, CullView.ForRange(origin, maxRange), Matrix4x4.identity);
    }

    public LidarIndirectDrawStats AddLidarDepthDrawCommands(CommandBuffer cmd, Material depthMaterial)
    {
        var stats = new LidarIndirectDrawStats { batchers = 1, sourceInstances = _instances.Count };

        if (!_ready || depthMaterial == null)
        {
            stats.skippedNotReady = 1;
            return stats;
        }

        stats.readyBatchers = 1;

        if (_instances.Count == 0)
        {
            stats.skippedNoInstances = 1;
            return stats;
        }

        if (_lidarResult == null || !_lidarResult.hasResults || _lidarResult.boundVersion != _dataVersion)
        {
            stats.skippedMissingBuffers = 1;
            return stats;
        }

        for (int lod = 0; lod < lods.Length; lod++)
        {
            if (_subMeshCounts[lod] == 0) continue;
            stats.lods++;

            for (int s = 0; s < _subMeshCounts[lod]; s++)
            {
                cmd.DrawMeshInstancedIndirect(
                    lods[lod].mesh, s, depthMaterial, 0, _lidarResult.args, ArgsOffset(lod, s), _lidarResult.props[lod]);
                stats.submeshes++;
                stats.queuedCommands++;
            }
        }

        return stats;
    }

    public string GetPositionDiagnosticSummary()
    {
        if (_instances.Count == 0)
            return $"{itemId}: no instances";

        float minLod0Y = float.PositiveInfinity;
        float maxLod0Y = float.NegativeInfinity;
        float minLod1DeltaY = float.PositiveInfinity;
        float maxLod1DeltaY = float.NegativeInfinity;
        foreach (InstanceData instance in _instances)
        {
            minLod0Y = Mathf.Min(minLod0Y, instance.lod0.position.y);
            maxLod0Y = Mathf.Max(maxLod0Y, instance.lod0.position.y);
            float lod1DeltaY = instance.lod1.position.y - instance.lod0.position.y;
            minLod1DeltaY = Mathf.Min(minLod1DeltaY, lod1DeltaY);
            maxLod1DeltaY = Mathf.Max(maxLod1DeltaY, lod1DeltaY);
        }

        return
            $"{itemId}: instances={_instances.Count}, " +
            $"lod0Y={minLod0Y:F3}..{maxLod0Y:F3}, " +
            $"lod1MinusLod0Y={minLod1DeltaY:F3}..{maxLod1DeltaY:F3}";
    }

    // Rebuilds instance buffers if needed; false when there is nothing to draw.
    private bool PrepareBuffers()
    {
        if (!_ready || !isActiveAndEnabled) return false;
        if (_buffersDirty) RebuildBuffers();
        return _instances.Count > 0;
    }

    private void RebuildBuffers()
    {
        _buffersDirty = false;
        _dataVersion++;
        ReleaseInstanceBuffers();

        int count = _instances.Count;
        if (count == 0) return;

        var positions = new Vector4[count];
        var lodRenderData = new LodRenderData[lods.Length][];
        for (int lod = 0; lod < lods.Length; lod++)
            lodRenderData[lod] = new LodRenderData[count];

        if (_spheres.Length != count)
            _spheres = new Vector4[count];

        for (int i = 0; i < count; i++)
        {
            InstanceData instance = _instances[i];
            Vector3 position = instance.lod0.position;
            positions[i] = position;
            _spheres[i] = CalculateBoundingSphere(instance);

            for (int lod = 0; lod < lods.Length; lod++)
            {
                LodTransform t = instance[lod];
                lodRenderData[lod][i] = new LodRenderData { rotation = t.rotation, scale = t.scale };
            }
        }

        _positionBuffer = new ComputeBuffer(count, sizeof(float) * 4);
        _positionBuffer.SetData(positions);
        _sphereBuffer = new ComputeBuffer(count, sizeof(float) * 4);
        _sphereBuffer.SetData(_spheres);

        _lodTransformBuffers = new ComputeBuffer[lods.Length];
        for (int lod = 0; lod < lods.Length; lod++)
        {
            _lodTransformBuffers[lod] = new ComputeBuffer(count, Marshal.SizeOf<LodRenderData>());
            _lodTransformBuffers[lod].SetData(lodRenderData[lod]);
        }

        RecalculateBounds();
    }

    private void ReleaseInstanceBuffers()
    {
        _positionBuffer?.Release();
        _sphereBuffer?.Release();
        if (_lodTransformBuffers != null)
            foreach (ComputeBuffer buffer in _lodTransformBuffers) buffer?.Release();
        _positionBuffer = null;
        _sphereBuffer = null;
        _lodTransformBuffers = null;
    }

    // Sphere around every LOD mesh as the shader draws it: all LODs share lod0's position.
    private Vector4 CalculateBoundingSphere(InstanceData instance)
    {
        Vector3 position = instance.lod0.position;
        Vector3 center = position;
        float radius = 0f;
        bool first = true;

        for (int lod = 0; lod < lods.Length; lod++)
        {
            Mesh mesh = lods[lod].mesh;
            if (mesh == null) continue;

            LodTransform t = instance[lod];
            Quaternion rotation = new Quaternion(t.rotation.x, t.rotation.y, t.rotation.z, t.rotation.w);
            Vector3 absScale = new Vector3(Mathf.Abs(t.scale.x), Mathf.Abs(t.scale.y), Mathf.Abs(t.scale.z));
            Vector3 lodCenter = position + rotation * Vector3.Scale(mesh.bounds.center, t.scale);
            float lodRadius = Vector3.Scale(mesh.bounds.extents, absScale).magnitude;

            if (first)
            {
                center = lodCenter;
                radius = lodRadius;
                first = false;
            }
            else
            {
                radius = Mathf.Max(radius, Vector3.Distance(center, lodCenter) + lodRadius);
            }
        }

        return new Vector4(center.x, center.y, center.z, radius);
    }

    private void RecalculateBounds()
    {
        Vector4 s = _spheres[0];
        _cullingBounds = new Bounds(s, Vector3.one * (2f * s.w));
        for (int i = 1; i < _instances.Count; i++)
        {
            s = _spheres[i];
            _cullingBounds.Encapsulate(new Bounds(s, Vector3.one * (2f * s.w)));
        }

        // The procedural shader multiplies its TRS by Unity's indirect-draw object matrix. Keep the draw
        // bounds centered at the origin so that matrix stays identity (a non-zero center double-translates).
        Vector3 min = _cullingBounds.min;
        Vector3 max = _cullingBounds.max;
        float originRadius = Mathf.Max(
            Mathf.Abs(min.x), Mathf.Abs(max.x),
            Mathf.Abs(min.y), Mathf.Abs(max.y),
            Mathf.Abs(min.z), Mathf.Abs(max.z));
        _drawBounds = new Bounds(Vector3.zero, Vector3.one * (originRadius * 2f + 0.4f));
    }

    // Whole-batch rejection before any per-instance work.
    private bool BoundsVisible(in CullView view)
    {
        float maxDistanceSq = view.IsRange ? view.rangeSq : HardCullDistanceSq;
        if (_cullingBounds.SqrDistance(view.origin) >= maxDistanceSq) return false;
        if (view.planes == null) return true;

        Vector3 min = _cullingBounds.min;
        Vector3 max = _cullingBounds.max;
        foreach (Vector4 plane in view.planes)
        {
            Vector3 positiveVertex = new Vector3(
                plane.x >= 0f ? max.x : min.x,
                plane.y >= 0f ? max.y : min.y,
                plane.z >= 0f ? max.z : min.z);

            if (plane.x * positiveVertex.x + plane.y * positiveVertex.y + plane.z * positiveVertex.z + plane.w < 0f)
                return false;
        }

        return true;
    }

    private void Cull(CommandBuffer cmd, CullResult result, in CullView view, Matrix4x4 viewKey)
    {
        BindResult(result);

        int flags = (view.planes != null ? CullFrustum : 0) | (view.IsRange ? CullRange : 0);
        int key = flags | (view.occlusion ? CullOcclusion : 0);
        if (result.hasResults &&
            result.flags == key &&
            result.viewKey == viewKey &&
            result.originKey == view.origin &&
            result.rangeKey == view.rangeSq)
            return;

        result.hasResults = true;
        result.flags = key;
        result.viewKey = viewKey;
        result.originKey = view.origin;
        result.rangeKey = view.rangeSq;
        if (view.planes != null)
            Array.Copy(view.planes, result.planes, result.planes.Length);
        result.lodMask = view.IsRange ? AllLodsMask : CalculateVisibleLodMask(view);
        if (result.lodMask == 0) return;

        RecordDispatch(cmd, result, flags, view.planes, view.origin, view.rangeSq, null);
    }

    private void RecordDispatch(
        CommandBuffer cmd,
        CullResult result,
        int flags,
        Vector4[] planes,
        Vector3 origin,
        float rangeSq,
        OcclusionView occlusion)
    {
        using (CullMarker.Auto())
        {
            for (int lod = 0; lod < MaxLods; lod++)
            {
                ComputeBuffer target = lod < lods.Length ? result.visible[lod] : _dummyAppendBuffer;
                if (lod < lods.Length) cmd.SetBufferCounterValue(target, 0);
                cmd.SetComputeBufferParam(_cullingShader, _kernel, LodBufIds[lod], target);
            }

            cmd.SetComputeBufferParam(_cullingShader, _kernel, PositionBufferId, _positionBuffer);
            cmd.SetComputeBufferParam(_cullingShader, _kernel, SphereBufferId, _sphereBuffer);
            cmd.SetComputeIntParam(_cullingShader, NumToDrawId, _instances.Count);
            cmd.SetComputeIntParam(_cullingShader, NumLodsId, lods.Length);
            cmd.SetComputeIntParam(_cullingShader, CullFlagsId, flags);
            cmd.SetComputeVectorParam(_cullingShader, LodDistancesId, _lodDistancesSq);
            cmd.SetComputeVectorParam(_cullingShader, CullOriginId, origin);
            cmd.SetComputeFloatParam(_cullingShader, RangeMaxDistanceSqId, rangeSq);
            if (planes != null)
                cmd.SetComputeVectorArrayParam(_cullingShader, PlanesId, planes);

            cmd.SetComputeTextureParam(
                _cullingShader, _kernel, HiZTextureId, occlusion != null ? occlusion.hiz : Texture2D.whiteTexture);
            if (occlusion != null)
            {
                cmd.SetComputeMatrixParam(_cullingShader, HiZWorldToViewId, occlusion.worldToView);
                cmd.SetComputeVectorParam(_cullingShader, HiZProjectionId, occlusion.projection);
                cmd.SetComputeVectorParam(_cullingShader, HiZSizeId, occlusion.size);
            }

            cmd.DispatchCompute(
                _cullingShader, _kernel, Mathf.CeilToInt(_instances.Count / (float)_threadGroupSize), 1, 1);

            for (int lod = 0; lod < lods.Length; lod++)
                for (int s = 0; s < _subMeshCounts[lod]; s++)
                    cmd.CopyCounterValue(result.visible[lod], result.args, (uint)(ArgsOffset(lod, s) + sizeof(uint)));
        }
    }

    // (Re)allocates a result's buffers for the current instance data and rebinds its draw properties.
    private void BindResult(CullResult result)
    {
        if (result.boundVersion == _dataVersion) return;

        int count = _instances.Count;
        if (result.visible == null || result.capacity < count)
        {
            result.Release();
            result.capacity = count;
            result.visible = new ComputeBuffer[lods.Length];
            for (int lod = 0; lod < lods.Length; lod++)
                result.visible[lod] = new ComputeBuffer(count, sizeof(uint), ComputeBufferType.Append);

            result.args = new ComputeBuffer(
                Mathf.Max(1, _argsEntryCount), ArgsStride * sizeof(uint), ComputeBufferType.IndirectArguments);
            if (_argsEntryCount > 0)
                result.args.SetData(_argsTemplate);
        }

        result.props ??= new MaterialPropertyBlock[lods.Length];
        for (int lod = 0; lod < lods.Length; lod++)
        {
            MaterialPropertyBlock props = result.props[lod] ??= new MaterialPropertyBlock();
            props.SetBuffer(VisibleIndicesId, result.visible[lod]);
            props.SetBuffer(PositionsId, _positionBuffer);
            props.SetBuffer(LodTransformDataId, _lodTransformBuffers[lod]);
        }

        result.boundVersion = _dataVersion;
        result.hasResults = false;
    }

    // CPU pass that mirrors the GPU test to find which LODs have any visible instance,
    // so empty LODs cost neither a dispatch nor a draw call.
    private int CalculateVisibleLodMask(in CullView view)
    {
        int mask = 0;
        int fullMask = (1 << lods.Length) - 1;
        float hardCullDistanceSq = HardCullDistanceSq;

        for (int i = 0; i < _instances.Count; i++)
        {
            if (view.planes != null && !SphereInFrustum(_spheres[i], view.planes)) continue;

            float distanceSq = (_instances[i].lod0.position - view.origin).sqrMagnitude;
            if (distanceSq - CpuCullEpsilon >= hardCullDistanceSq) continue;

            mask |= (1 << SelectLod(distanceSq - CpuCullEpsilon)) | (1 << SelectLod(distanceSq + CpuCullEpsilon));
            if (mask == fullMask) break;
        }

        return mask & fullMask;
    }

    private static bool SphereInFrustum(Vector4 sphere, Vector4[] planes)
    {
        foreach (Vector4 plane in planes)
        {
            if (plane.x * sphere.x + plane.y * sphere.y + plane.z * sphere.z + plane.w < -sphere.w - CpuCullEpsilon)
                return false;
        }

        return true;
    }

    private int SelectLod(float distanceSq)
    {
        for (int lod = 0; lod < lods.Length - 1; lod++)
        {
            if (distanceSq < _lodDistancesSq[lod])
                return lod;
        }

        return lods.Length - 1;
    }

    private int ArgsOffset(int lod, int subMesh) =>
        (_argsEntryStart[lod] + subMesh) * ArgsStride * sizeof(uint);
}
