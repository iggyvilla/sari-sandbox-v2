using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

// What to cull against: a camera (optional frustum + LOD hard-cull distance) or a LiDAR range sphere.
public readonly struct CullView
{
    public readonly Vector4[] planes; // null = no frustum test
    public readonly Vector3 origin;
    public readonly float rangeSq;    // > 0 = LiDAR range mode
    public readonly bool occlusion;   // an occlusion pass will re-cull these results this frame

    private CullView(Vector4[] planes, Vector3 origin, float rangeSq, bool occlusion)
    {
        this.planes = planes;
        this.origin = origin;
        this.rangeSq = rangeSq;
        this.occlusion = occlusion;
    }

    public bool IsRange => rangeSq > 0f;

    public static CullView ForCamera(Vector3 origin, Vector4[] frustumPlanes, bool occlusion) =>
        new(frustumPlanes, origin, 0f, occlusion);

    public static CullView ForRange(Vector3 origin, float range) =>
        new(null, origin, Mathf.Max(0.01f, range) * Mathf.Max(0.01f, range), false);
}

// This frame's Hi-Z pyramid for one camera (see HiZOcclusionFeature).
public sealed class OcclusionView
{
    public Texture hiz;
    public Matrix4x4 worldToView;
    public Vector4 projection; // proj[0][0], proj[1][1], near, depth bias
    public Vector4 size;       // width, height, mip count
}

// GPU-driven culling for every BatchInstancer: one Cull dispatch per viewer covers all instances, writing
// compact per-(batch, LOD) visible lists and the indirect args every batch draws from.
//
// Draw data is shared by all batches: _Positions (one entry per instance) and _LodTransformData (one entry per
// instance and LOD, laid out like the visible lists). A visible-list entry is the LOD data slot to draw, so
// a draw only needs the start of its (batch, LOD) region in the list. With `IndirectArgs` that start is the args'
// startInstance and one property block per viewer serves every draw (Graphics.RenderMeshIndirect); without it
// each draw carries its own offset in a property block (DrawMeshInstancedIndirect).
public sealed class InstanceCullingSystem : IDisposable
{
    // Keep in sync with FrustumCullingFilterer.compute.
    private const int CullFrustum = 1;
    private const int CullRange = 2;
    private const int CullOcclusion = 4;
    private const int ThreadGroupSize = 64;
    private const int MaxBatchSize = 1 << 16;

    [StructLayout(LayoutKind.Sequential)]
    private struct InstanceCullData
    {
        public Vector4 sphere;
        public Vector3 position;
        public uint id; // batch index << 16 | index within the batch
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatchCullData
    {
        public Vector4 lodDistancesSq;
        public uint numLods;
        public uint visibleOffset;
        public uint count;
        public uint firstInstance;
    }

    // GPU results for one viewer (a camera or the LiDAR), so viewers never share counts.
    private sealed class ViewerResult
    {
        public readonly bool lidar; // LiDAR draws from a command buffer: always per-draw offsets
        public ComputeBuffer visible;
        public ComputeBuffer counts;
        public ComputeBuffer instanceLods;
        public GraphicsBuffer args;
        public MaterialPropertyBlock drawProps;  // all draws (IndirectArgs)
        public MaterialPropertyBlock[][] props;  // [batch][lod], with the region offset (legacy draws, LiDAR)
        public int[] lodMasks;                   // per batch; 0 = not drawn
        public int boundVersion = -1;
        public bool hasResults;
        public int key;
        public Matrix4x4 viewKey;
        public Vector3 originKey;
        public float rangeKey;
        public int drawnFrame = -1;
        public readonly Vector4[] planes = new Vector4[6];

        public ViewerResult(bool lidar) => this.lidar = lidar;

        public void Release()
        {
            visible?.Release();
            counts?.Release();
            instanceLods?.Release();
            args?.Release();
            visible = counts = instanceLods = null;
            args = null;
        }
    }

    private static readonly ProfilerMarker CullMarker = new("Frustum Culling");
    private static readonly int InstancesId = Shader.PropertyToID("instances");
    private static readonly int BatchesId = Shader.PropertyToID("batches");
    private static readonly int ArgsSourcesId = Shader.PropertyToID("args_sources");
    private static readonly int CountsId = Shader.PropertyToID("counts");
    private static readonly int VisibleId = Shader.PropertyToID("visible");
    private static readonly int InstanceLodsId = Shader.PropertyToID("instance_lods");
    private static readonly int ArgsId = Shader.PropertyToID("args");
    private static readonly int NumInstancesId = Shader.PropertyToID("num_instances");
    private static readonly int NumCountersId = Shader.PropertyToID("num_counters");
    private static readonly int NumArgsId = Shader.PropertyToID("num_args");
    private static readonly int PlanesId = Shader.PropertyToID("planes");
    private static readonly int CullFlagsId = Shader.PropertyToID("cull_flags");
    private static readonly int CullOriginId = Shader.PropertyToID("cull_origin");
    private static readonly int RangeMaxDistanceSqId = Shader.PropertyToID("range_max_distance_sq");
    private static readonly int HiZTextureId = Shader.PropertyToID("hiz_texture");
    private static readonly int HiZWorldToViewId = Shader.PropertyToID("hiz_world_to_view");
    private static readonly int HiZProjectionId = Shader.PropertyToID("hiz_projection");
    private static readonly int HiZSizeId = Shader.PropertyToID("hiz_size");
    private static readonly int VisibleIndicesId = Shader.PropertyToID("_VisibleIndices");
    private static readonly int VisibleOffsetId = Shader.PropertyToID("_VisibleOffset");
    private static readonly int PositionsId = Shader.PropertyToID("_Positions");
    private static readonly int LodTransformDataId = Shader.PropertyToID("_LodTransformData");

    private readonly ComputeShader _shader;
    private readonly int _clearKernel;
    private readonly int _cullKernel;
    private readonly int _compactKernel;
    private readonly int _argsKernel;

    private readonly List<BatchInstancer> _batchers = new();
    private readonly List<IndirectDraw> _draws = new();
    private readonly List<int> _seenVersions = new();
    private readonly Dictionary<Camera, ViewerResult> _cameras = new();
    private readonly List<Camera> _destroyedCameras = new();
    private ViewerResult _lidar;

    private ComputeBuffer _instanceBuffer;
    private ComputeBuffer _batchBuffer;
    private ComputeBuffer _argsSourceBuffer;
    private ComputeBuffer _positionBuffer;
    private ComputeBuffer _lodDataBuffer;
    private GraphicsBuffer.IndirectDrawIndexedArgs[] _argsTemplate = Array.Empty<GraphicsBuffer.IndirectDrawIndexedArgs>();
    private GraphicsBuffer.IndirectDrawIndexedArgs[] _argsTemplateNoStart = Array.Empty<GraphicsBuffer.IndirectDrawIndexedArgs>();
    private Vector4[] _positions = Array.Empty<Vector4>();
    private LodRenderData[] _lodData = Array.Empty<LodRenderData>();
    private int[] _drawOrder = Array.Empty<int>();
    private int[] _argsStart = Array.Empty<int>();
    private uint[] _visibleOffsets = Array.Empty<uint>();
    private bool _indirectArgs;
    private bool _layoutDirty;
    private int _instanceCount;
    private int _visibleSize;
    private int _argsEntries;
    private int _version;

    public InstanceCullingSystem(ComputeShader shader)
    {
        _shader = shader;
        _clearKernel = shader.FindKernel("ClearCounts");
        _cullKernel = shader.FindKernel("Cull");
        _compactKernel = shader.FindKernel("Compact");
        _argsKernel = shader.FindKernel("WriteArgs");
        Shader.SetGlobalFloat(VisibleOffsetId, -1f); // no per-draw offset: the region start is the args' startInstance
    }

    public IReadOnlyList<BatchInstancer> Batchers => _batchers;

    // Draw with Graphics.RenderMeshIndirect, region offsets in the args and no per-draw property block.
    public bool IndirectArgs
    {
        get => _indirectArgs;
        set
        {
            if (_indirectArgs == value) return;
            _indirectArgs = value;
            _layoutDirty = true;
        }
    }

    public void Add(BatchInstancer batcher)
    {
        _batchers.Add(batcher);
        _seenVersions.Add(-1);
    }

    public void RenderCamera(CommandBuffer cmd, Camera cam, in CullView view)
    {
        if (!Prepare()) return;

        if (!_cameras.TryGetValue(cam, out ViewerResult viewer))
            _cameras[cam] = viewer = new ViewerResult(false);

        Cull(cmd, viewer, view, cam.cullingMatrix);
        viewer.drawnFrame = Time.frameCount;
        Draw(cam, viewer);
    }

    // Draws another camera's latest culling results into cam (debug view); false if source has none.
    public bool RenderCameraAs(Camera cam, Camera source)
    {
        if (source == null || !_cameras.TryGetValue(source, out ViewerResult viewer) ||
            !viewer.hasResults || viewer.boundVersion != _version || HasPendingChanges())
            return false;

        Draw(cam, viewer);
        return true;
    }

    private void Draw(Camera cam, ViewerResult viewer)
    {
        foreach (int index in _drawOrder)
        {
            IndirectDraw draw = _draws[index];
            if ((viewer.lodMasks[draw.batch] & (1 << draw.lod)) == 0) continue;

            if (_indirectArgs)
            {
                var renderParams = new RenderParams(draw.material)
                {
                    worldBounds = draw.bounds,
                    camera = cam,
                    shadowCastingMode = BatchInstancer.ProductShadowMode,
                    receiveShadows = true,
                    lightProbeUsage = LightProbeUsage.BlendProbes,
                    matProps = viewer.drawProps
                };
                Graphics.RenderMeshIndirect(renderParams, draw.mesh, viewer.args, 1, index);
            }
            else
            {
                Graphics.DrawMeshInstancedIndirect(
                    draw.mesh, draw.submesh, draw.material, draw.bounds, viewer.args, index * BatchInstancer.ArgsBytes,
                    viewer.props[draw.batch][draw.lod], BatchInstancer.ProductShadowMode, true, 0, cam);
            }
        }
    }

    // True when batches changed (or were added) since the shared buffers were built; draws would be stale.
    private bool HasPendingChanges()
    {
        if (_layoutDirty) return true;
        for (int i = 0; i < _batchers.Count; i++)
            if (_seenVersions[i] != _batchers[i].DataVersion) return true;
        return false;
    }

    // Re-culls a camera's results against its Hi-Z so the opaque pass skips hidden instances.
    public void RecordOcclusionCull(CommandBuffer cmd, Camera cam, OcclusionView occlusion)
    {
        if (!_cameras.TryGetValue(cam, out ViewerResult viewer) ||
            !viewer.hasResults || viewer.boundVersion != _version)
            return;

        int flags = (viewer.key & CullFrustum) | CullOcclusion;
        RecordDispatch(
            cmd, viewer, flags, (flags & CullFrustum) != 0 ? viewer.planes : null, viewer.originKey, 0f, occlusion);
    }

    public void CullForLidarRange(CommandBuffer cmd, Vector3 origin, float maxRange)
    {
        if (!Prepare()) return;

        _lidar ??= new ViewerResult(true);
        Cull(cmd, _lidar, CullView.ForRange(origin, maxRange), Matrix4x4.identity);
    }

    public LidarIndirectDrawStats AddLidarDepthDrawCommands(CommandBuffer cmd, Material depthMaterial)
    {
        var stats = new LidarIndirectDrawStats();
        bool ready = _lidar != null && _lidar.hasResults && _lidar.boundVersion == _version;
        for (int i = 0; i < _batchers.Count; i++)
        {
            stats.Add(_batchers[i].AddLidarDepthDrawCommands(
                cmd,
                depthMaterial,
                ready ? _lidar.args : null,
                _argsStart.Length > i ? _argsStart[i] : 0,
                ready ? _lidar.props[i] : null));
        }
        return stats;
    }

    // Debug: instances a camera drew on its last render (synchronous GPU readback; don't call per frame).
    public int ReadVisibleCount(Camera cam)
    {
        if (!_cameras.TryGetValue(cam, out ViewerResult viewer) || !viewer.hasResults ||
            viewer.drawnFrame < Time.frameCount - 1 || viewer.boundVersion != _version ||
            viewer.lodMasks.Length != _batchers.Count)
            return 0;

        var counts = new uint[_batchers.Count * BatchInstancer.MaxLods];
        viewer.counts.GetData(counts);
        int total = 0;
        for (int batch = 0; batch < _batchers.Count; batch++)
            for (int lod = 0; lod < BatchInstancer.MaxLods; lod++)
                if ((viewer.lodMasks[batch] & (1 << lod)) != 0)
                    total += (int)counts[batch * BatchInstancer.MaxLods + lod];
        return total;
    }

    public void ReleaseDestroyedCameras()
    {
        foreach (Camera cam in _cameras.Keys)
            if (cam == null) _destroyedCameras.Add(cam);

        foreach (Camera cam in _destroyedCameras)
        {
            _cameras[cam].Release();
            _cameras.Remove(cam);
        }
        _destroyedCameras.Clear();
    }

    public void Dispose()
    {
        foreach (ViewerResult viewer in _cameras.Values) viewer.Release();
        _cameras.Clear();
        _lidar?.Release();
        ReleaseSharedBuffers();
    }

    // Rebuilds the shared culling buffers when any batch changed; false when there is nothing to cull.
    private bool Prepare()
    {
        bool dirty = _layoutDirty;
        for (int i = 0; i < _batchers.Count; i++)
        {
            if (_seenVersions[i] == _batchers[i].DataVersion) continue;
            _seenVersions[i] = _batchers[i].DataVersion;
            _batchers[i].PrepareBounds();
            dirty = true;
        }

        if (dirty) RebuildSharedBuffers();
        return _instanceCount > 0;
    }

    private void RebuildSharedBuffers()
    {
        ReleaseSharedBuffers();
        _layoutDirty = false;
        _version++;
        _draws.Clear();

        var instances = new List<InstanceCullData>();
        var batches = new BatchCullData[_batchers.Count];
        _argsStart = new int[_batchers.Count];
        _visibleOffsets = new uint[_batchers.Count];
        _visibleSize = 0;

        for (int batch = 0; batch < _batchers.Count; batch++)
        {
            BatchInstancer batcher = _batchers[batch];
            int count = batcher.InstanceCount;
            if (count >= MaxBatchSize)
                Debug.LogError($"InstanceCullingSystem: '{batcher.itemId}' exceeds {MaxBatchSize} instances.");

            _visibleOffsets[batch] = (uint)_visibleSize;
            batches[batch] = new BatchCullData
            {
                lodDistancesSq = batcher.LodDistancesSq,
                numLods = (uint)batcher.LodCount,
                visibleOffset = (uint)_visibleSize,
                count = (uint)count,
                firstInstance = (uint)instances.Count
            };
            _visibleSize += count * batcher.LodCount;

            for (int i = 0; i < count; i++)
            {
                instances.Add(new InstanceCullData
                {
                    sphere = batcher.Spheres[i],
                    position = batcher.Instances[i].lod0.position,
                    id = (uint)(batch << 16 | i)
                });
            }

            _argsStart[batch] = _draws.Count;
            batcher.AppendDraws(_draws, batch, _visibleOffsets[batch]);
        }

        _instanceCount = instances.Count;
        _argsEntries = _draws.Count;
        BuildDrawOrder();
        BuildArgsTemplates();
        if (_instanceCount == 0) return;

        _instanceBuffer = new ComputeBuffer(_instanceCount, Marshal.SizeOf<InstanceCullData>());
        _instanceBuffer.SetData(instances);
        _batchBuffer = new ComputeBuffer(batches.Length, Marshal.SizeOf<BatchCullData>());
        _batchBuffer.SetData(batches);
        _argsSourceBuffer = new ComputeBuffer(Mathf.Max(1, _argsEntries), sizeof(uint));
        if (_argsEntries > 0)
            _argsSourceBuffer.SetData(_draws.ConvertAll(d => (uint)d.Counter));
        UploadDrawData();
    }

    private void BuildArgsTemplates()
    {
        _argsTemplate = new GraphicsBuffer.IndirectDrawIndexedArgs[_argsEntries];
        _argsTemplateNoStart = new GraphicsBuffer.IndirectDrawIndexedArgs[_argsEntries];
        for (int i = 0; i < _argsEntries; i++)
        {
            IndirectDraw draw = _draws[i];
            var args = new GraphicsBuffer.IndirectDrawIndexedArgs
            {
                indexCountPerInstance = draw.mesh.GetIndexCount(draw.submesh),
                startIndex = draw.mesh.GetIndexStart(draw.submesh),
                baseVertexIndex = draw.mesh.GetBaseVertex(draw.submesh)
            };
            _argsTemplateNoStart[i] = args;
            args.startInstance = draw.startInstance;
            _argsTemplate[i] = args;
        }
    }

    // Draw order: with IndirectArgs, opaque draws sharing a material are contiguous (then by mesh) so the
    // renderer changes state as little as possible. Transparent draws keep batch order.
    private void BuildDrawOrder()
    {
        int count = _draws.Count;
        _drawOrder = new int[count];
        for (int i = 0; i < count; i++) _drawOrder[i] = i;
        if (!_indirectArgs) return;

        var materialRank = new Dictionary<Material, int>();
        var meshRank = new Dictionary<Mesh, int>();
        var rank = new (int queue, int material, int mesh)[count];
        for (int i = 0; i < count; i++)
        {
            IndirectDraw draw = _draws[i];
            int queue = draw.material.renderQueue;
            bool opaque = queue <= (int)RenderQueue.GeometryLast;
            if (!materialRank.TryGetValue(draw.material, out int material)) materialRank[draw.material] = material = materialRank.Count;
            if (!meshRank.TryGetValue(draw.mesh, out int mesh)) meshRank[draw.mesh] = mesh = meshRank.Count;
            rank[i] = (queue, opaque ? material : 0, opaque ? mesh : 0);
        }

        Array.Sort(_drawOrder, (a, b) =>
        {
            int c = rank[a].queue.CompareTo(rank[b].queue);
            if (c == 0) c = rank[a].material.CompareTo(rank[b].material);
            if (c == 0) c = rank[a].mesh.CompareTo(rank[b].mesh);
            return c != 0 ? c : a.CompareTo(b);
        });
    }

    // Positions per instance and LOD data per (instance, LOD) slot, shared by every draw.
    private void UploadDrawData()
    {
        if (_positions.Length < _instanceCount) _positions = new Vector4[_instanceCount];
        if (_lodData.Length < _visibleSize) _lodData = new LodRenderData[_visibleSize];

        int first = 0;
        for (int batch = 0; batch < _batchers.Count; batch++)
        {
            _batchers[batch].WriteDrawData(_positions, _lodData, first, (int)_visibleOffsets[batch]);
            first += _batchers[batch].InstanceCount;
        }

        _positionBuffer = new ComputeBuffer(_instanceCount, sizeof(float) * 4);
        _positionBuffer.SetData(_positions, 0, 0, _instanceCount);
        _lodDataBuffer = new ComputeBuffer(Mathf.Max(1, _visibleSize), Marshal.SizeOf<LodRenderData>());
        _lodDataBuffer.SetData(_lodData, 0, 0, _visibleSize);
    }

    private void ReleaseSharedBuffers()
    {
        _instanceBuffer?.Release();
        _batchBuffer?.Release();
        _argsSourceBuffer?.Release();
        _positionBuffer?.Release();
        _lodDataBuffer?.Release();
        _instanceBuffer = _batchBuffer = _argsSourceBuffer = _positionBuffer = _lodDataBuffer = null;
    }

    private void Cull(CommandBuffer cmd, ViewerResult viewer, in CullView view, Matrix4x4 viewKey)
    {
        BindViewer(viewer);

        int flags = (view.planes != null ? CullFrustum : 0) | (view.IsRange ? CullRange : 0);
        int key = flags | (view.occlusion ? CullOcclusion : 0);
        if (viewer.hasResults &&
            viewer.key == key &&
            viewer.viewKey == viewKey &&
            viewer.originKey == view.origin &&
            viewer.rangeKey == view.rangeSq)
            return;

        viewer.hasResults = true;
        viewer.key = key;
        viewer.viewKey = viewKey;
        viewer.originKey = view.origin;
        viewer.rangeKey = view.rangeSq;
        if (view.planes != null)
            Array.Copy(view.planes, viewer.planes, viewer.planes.Length);

        bool anyVisible = false;
        for (int i = 0; i < _batchers.Count; i++)
        {
            BatchInstancer batcher = _batchers[i];
            viewer.lodMasks[i] = batcher.IsDrawable && batcher.BoundsVisible(view)
                ? batcher.CalculateVisibleLodMask(view)
                : 0;
            anyVisible |= viewer.lodMasks[i] != 0;
        }

        if (anyVisible)
            RecordDispatch(cmd, viewer, flags, view.planes, view.origin, view.rangeSq, null);
    }

    // (Re)allocates a viewer's buffers for the current shared data and rebinds its draw properties.
    private void BindViewer(ViewerResult viewer)
    {
        if (viewer.boundVersion == _version) return;

        viewer.Release();
        bool regionInArgs = _indirectArgs && !viewer.lidar;
        viewer.visible = new ComputeBuffer(Mathf.Max(1, _visibleSize), sizeof(uint));
        viewer.counts = new ComputeBuffer(Mathf.Max(1, _batchers.Count * BatchInstancer.MaxLods), sizeof(uint));
        viewer.instanceLods = new ComputeBuffer(Mathf.Max(1, _instanceCount), sizeof(uint));
        viewer.args = new GraphicsBuffer(
            GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Raw,
            Mathf.Max(1, _argsEntries), BatchInstancer.ArgsBytes);
        if (_argsEntries > 0)
            viewer.args.SetData(regionInArgs ? _argsTemplate : _argsTemplateNoStart);

        viewer.lodMasks = new int[_batchers.Count];
        viewer.props = null;
        viewer.drawProps = null;
        if (regionInArgs)
        {
            viewer.drawProps = CreateProps(viewer, -1f);
        }
        else
        {
            viewer.props = new MaterialPropertyBlock[_batchers.Count][];
            for (int batch = 0; batch < _batchers.Count; batch++)
            {
                BatchInstancer batcher = _batchers[batch];
                if (batcher.InstanceCount == 0) continue;

                viewer.props[batch] = new MaterialPropertyBlock[batcher.LodCount];
                for (int lod = 0; lod < batcher.LodCount; lod++)
                    viewer.props[batch][lod] = CreateProps(viewer, _visibleOffsets[batch] + lod * batcher.InstanceCount);
            }
        }

        viewer.boundVersion = _version;
        viewer.hasResults = false;
    }

    // offset < 0: no per-draw region offset (the shader takes it from the indirect args).
    private MaterialPropertyBlock CreateProps(ViewerResult viewer, float offset)
    {
        var props = new MaterialPropertyBlock();
        props.SetBuffer(VisibleIndicesId, viewer.visible);
        props.SetBuffer(PositionsId, _positionBuffer);
        props.SetBuffer(LodTransformDataId, _lodDataBuffer);
        if (offset >= 0f) props.SetFloat(VisibleOffsetId, offset);
        return props;
    }

    private void RecordDispatch(
        CommandBuffer cmd,
        ViewerResult viewer,
        int flags,
        Vector4[] planes,
        Vector3 origin,
        float rangeSq,
        OcclusionView occlusion)
    {
        using (CullMarker.Auto())
        {
            int counters = _batchers.Count * BatchInstancer.MaxLods;
            cmd.SetComputeIntParam(_shader, NumCountersId, counters);
            cmd.SetComputeBufferParam(_shader, _clearKernel, CountsId, viewer.counts);
            cmd.DispatchCompute(_shader, _clearKernel, GroupCount(counters), 1, 1);

            cmd.SetComputeBufferParam(_shader, _cullKernel, InstancesId, _instanceBuffer);
            cmd.SetComputeBufferParam(_shader, _cullKernel, BatchesId, _batchBuffer);
            cmd.SetComputeBufferParam(_shader, _cullKernel, InstanceLodsId, viewer.instanceLods);
            cmd.SetComputeIntParam(_shader, NumInstancesId, _instanceCount);
            cmd.SetComputeIntParam(_shader, CullFlagsId, flags);
            cmd.SetComputeVectorParam(_shader, CullOriginId, origin);
            cmd.SetComputeFloatParam(_shader, RangeMaxDistanceSqId, rangeSq);
            if (planes != null)
                cmd.SetComputeVectorArrayParam(_shader, PlanesId, planes);

            cmd.SetComputeTextureParam(
                _shader, _cullKernel, HiZTextureId, occlusion != null ? occlusion.hiz : Texture2D.whiteTexture);
            if (occlusion != null)
            {
                cmd.SetComputeMatrixParam(_shader, HiZWorldToViewId, occlusion.worldToView);
                cmd.SetComputeVectorParam(_shader, HiZProjectionId, occlusion.projection);
                cmd.SetComputeVectorParam(_shader, HiZSizeId, occlusion.size);
            }
            cmd.DispatchCompute(_shader, _cullKernel, GroupCount(_instanceCount), 1, 1);

            cmd.SetComputeBufferParam(_shader, _compactKernel, BatchesId, _batchBuffer);
            cmd.SetComputeBufferParam(_shader, _compactKernel, InstanceLodsId, viewer.instanceLods);
            cmd.SetComputeBufferParam(_shader, _compactKernel, CountsId, viewer.counts);
            cmd.SetComputeBufferParam(_shader, _compactKernel, VisibleId, viewer.visible);
            cmd.DispatchCompute(_shader, _compactKernel, Mathf.Max(1, _batchers.Count), 1, 1);

            cmd.SetComputeIntParam(_shader, NumArgsId, _argsEntries);
            cmd.SetComputeBufferParam(_shader, _argsKernel, ArgsSourcesId, _argsSourceBuffer);
            cmd.SetComputeBufferParam(_shader, _argsKernel, CountsId, viewer.counts);
            cmd.SetComputeBufferParam(_shader, _argsKernel, ArgsId, viewer.args);
            cmd.DispatchCompute(_shader, _argsKernel, GroupCount(_argsEntries), 1, 1);
        }
    }

    private static int GroupCount(int threads) => Mathf.Max(1, (threads + ThreadGroupSize - 1) / ThreadGroupSize);
}
