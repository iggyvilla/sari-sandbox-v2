using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Profiling;

// Per-LOD transform uploaded to the GPU.
// Matches the HLSL LodTransform struct layout exactly.
[StructLayout(LayoutKind.Sequential)]
public struct LodTransform
{
    public Vector3 position;
    public Vector4 rotation;
    public Vector3 scale;
}

// CPU-side source data: one independent transform per LOD level.
// lods[0] = _LOD0 (always present — base / farthest), higher indices = closer / higher quality.
[StructLayout(LayoutKind.Sequential)]
public struct InstanceData : IEquatable<InstanceData>
{
    public LodTransform lod0;   // _LOD0 (required)
    public LodTransform lod1;   // _LOD1 (optional; zeroed if absent — shader won't index it)
    public LodTransform lod2;   // _LOD2 (optional)
    public LodTransform lod3;   // _LOD3 (optional)

    public bool Equals(InstanceData other)
    {
        return lod0.position == other.lod0.position &&
               lod0.rotation == other.lod0.rotation &&
               lod0.scale    == other.lod0.scale    &&
               lod1.position == other.lod1.position &&
               lod1.rotation == other.lod1.rotation &&
               lod1.scale    == other.lod1.scale    &&
               lod2.position == other.lod2.position &&
               lod2.rotation == other.lod2.rotation &&
               lod2.scale    == other.lod2.scale    &&
               lod3.position == other.lod3.position &&
               lod3.rotation == other.lod3.rotation &&
               lod3.scale    == other.lod3.scale;
    }

    public override int GetHashCode() =>
        HashCode.Combine(lod0.position, lod0.rotation, lod0.scale,
                         lod1.position, lod1.rotation, lod1.scale);
}

[StructLayout(LayoutKind.Sequential)]
public struct LodRenderData
{
    public Vector4 rotation;
    public Vector4 scale;
}

// Describes one LOD level: the mesh, its cloned instancing materials, and the max distance
// at which this LOD is used. Switch to the next (lower-quality) LOD when dist >= maxDistance.
// maxDistance is unused on the last entry (it's the catch-all farthest LOD).
[System.Serializable]
public struct LODDefinition
{
    public Mesh mesh;
    public Material[] materials;
    [Tooltip("Use this LOD when closer than this distance. Unused on the last (farthest) LOD.")]
    public float maxDistance;
}

public class BatchInstancer : MonoBehaviour
{
    public Camera agentCamera;

    // LOD table: lods[0] = _LOD0 (highest quality, closest), lods[N-1] = farthest.
    // Set by GPUInstanceTracker.PrepareBatchInstancer, or configured directly in the Inspector.
    public LODDefinition[] lods;

    private List<InstanceData> instances = new();
    public string itemId;

    private ComputeBuffer _positionBuffer;
    private ComputeBuffer[] _lodTransformBuffers;
    private readonly int _positionDataSize = sizeof(float) * 4;
    private readonly int _lodRenderDataSize = sizeof(float) * 8;
    private readonly int _visibleIndexSize = sizeof(uint);

    private int _sVisibleIndicesId;
    private int _sPositionsId;
    private int _sLodTransformDataId;

    public ComputeShader frustumCullingShader;
    private ComputeBuffer _simplePlaneBuffer;

    // One visible-index AppendBuffer per active LOD; _dummyBuffer fills unused named HLSL slots
    private ComputeBuffer[] _visibleIndexBuffers;
    private ComputeBuffer _dummyBuffer;
    private SubMeshInstance[][] _subMeshPerLOD;

    private readonly int _simplePlaneSize = sizeof(float) * 4;

    private int _fPositionBufferId;
    private int _fNumToDrawId;
    private int _fPlanesId;
    private int _fLodDistancesId;
    private int _fNumLodsId;
    private int _fAgentPositionId;
    private int _fKernelId;
    private uint _fThreadGroupSizeX;

    // Hi-Z occlusion bindings (values come from HiZOcclusionManager each frame)
    private int _fHizTextureId;
    private int _fOcclusionEnabledId;
    private int _fHizMipCountId;
    private int _fHizTextureSizeId;
    private int _fHizViewProjId;
    private int _fHizProjScaleId;
    private int _fCameraPositionId;
    private int _fDepthBiasId;
    private int _fHizFlipYId;

    // Metal requires every declared texture slot bound even when the branch is off
    private static Texture2D _dummyHiZTexture;

    // Matches the 4 named AppendStructuredBuffer outputs in the compute shader
    private static readonly string[] LodBufNames = { "lod0_buf", "lod1_buf", "lod2_buf", "lod3_buf" };
    private readonly int[] _fLodBufIds = new int[LodBufNames.Length];

    private const int MAX_LODS = 4;

    private bool _ready = false;
    private bool _buffersDirty = false;

    // Read by OcclusionDebugger to bind debug buffers / read visible counts
    public int CullingKernel => _fKernelId;
    public int ActiveLodCount => lods != null ? lods.Length : 0;
    public int InstanceCount => instances.Count;
    public ComputeBuffer GetVisibleIndexBuffer(int lod) =>
        _visibleIndexBuffers != null && lod >= 0 && lod < _visibleIndexBuffers.Length
            ? _visibleIndexBuffers[lod]
            : null;

    public void Init()
    {
        if (lods == null || lods.Length == 0)
        {
            Debug.LogError($"BatchInstancer ({itemId}): no LODs defined.");
            return;
        }

        // Build SubMeshInstance arrays for each active LOD
        _subMeshPerLOD = new SubMeshInstance[lods.Length][];
        for (int i = 0; i < lods.Length; i++)
        {
            Mesh m = lods[i].mesh;
            Material[] mats = lods[i].materials;
            if (m == null || mats == null) continue;

            int count = Mathf.Min(mats.Length, m.subMeshCount);
            _subMeshPerLOD[i] = new SubMeshInstance[count];
            for (int s = 0; s < count; s++)
            {
                _subMeshPerLOD[i][s] = new SubMeshInstance(
                    m.GetIndexCount(s),
                    m.GetIndexStart(s),
                    m.GetBaseVertex(s),
                    mats[s]
                );
            }
        }

        _simplePlaneBuffer = new ComputeBuffer(6, _simplePlaneSize);

        _fPositionBufferId = Shader.PropertyToID("position_buffer");
        _fNumToDrawId     = Shader.PropertyToID("num_to_draw");
        _fPlanesId        = Shader.PropertyToID("planes");
        _fLodDistancesId  = Shader.PropertyToID("lod_distances");
        _fNumLodsId       = Shader.PropertyToID("num_lods");
        _fAgentPositionId = Shader.PropertyToID("agent_position");

        _fHizTextureId       = Shader.PropertyToID("hiz_texture");
        _fOcclusionEnabledId = Shader.PropertyToID("occlusion_enabled");
        _fHizMipCountId      = Shader.PropertyToID("hiz_mip_count");
        _fHizTextureSizeId   = Shader.PropertyToID("hiz_texture_size");
        _fHizViewProjId      = Shader.PropertyToID("hiz_view_proj");
        _fHizProjScaleId     = Shader.PropertyToID("hiz_proj_scale");
        _fCameraPositionId   = Shader.PropertyToID("camera_position");
        _fDepthBiasId        = Shader.PropertyToID("occlusion_depth_bias");
        _fHizFlipYId         = Shader.PropertyToID("hiz_flip_y");

        if (_dummyHiZTexture == null)
        {
            _dummyHiZTexture = new Texture2D(1, 1, TextureFormat.RFloat, false);
            _dummyHiZTexture.SetPixel(0, 0, Color.clear);
            _dummyHiZTexture.Apply();
        }

        for (int i = 0; i < LodBufNames.Length; i++)
            _fLodBufIds[i] = Shader.PropertyToID(LodBufNames[i]);

        _fKernelId = frustumCullingShader.FindKernel("CSMain");
        frustumCullingShader.GetKernelThreadGroupSizes(_fKernelId, out _fThreadGroupSizeX, out _, out _);
        _sVisibleIndicesId   = Shader.PropertyToID("_VisibleIndices");
        _sPositionsId        = Shader.PropertyToID("_Positions");
        _sLodTransformDataId = Shader.PropertyToID("_LodTransformData");

        // Assemble squared lod_distances: lods[i].maxDistance^2 for all N LODs.
        // Indices 0..N-2 drive LOD selection; index N-1 is the hard cull distance.
        // All stored ascending (inner → outer) as a float4.
        Vector4 lodDist = Vector4.zero;
        for (int i = 0; i < Mathf.Min(lods.Length, MAX_LODS); i++)
            lodDist[i] = lods[i].maxDistance * lods[i].maxDistance;

        frustumCullingShader.SetBuffer(_fKernelId, _fPlanesId, _simplePlaneBuffer);
        frustumCullingShader.SetVector(_fLodDistancesId, lodDist);
        frustumCullingShader.SetInt(_fNumLodsId, lods.Length);

        // Dummy 1-element buffer for HLSL slots beyond the active LOD count
        _dummyBuffer = new ComputeBuffer(1, _visibleIndexSize, ComputeBufferType.Append);
        for (int i = lods.Length; i < MAX_LODS; i++)
            frustumCullingShader.SetBuffer(_fKernelId, _fLodBufIds[i], _dummyBuffer);

        _ready = true;
    }

    void Update()
    {
        if (!_ready) return;

        if (_buffersDirty && instances.Count > 0)
            RebuildBuffers();

        if (instances.Count == 0) return;

        Profiler.BeginSample("Get Planes");
        SimplePlane[] planes = GPUInstanceTracker.Instance.cameraFrustumPlanes;
        _simplePlaneBuffer.SetData(planes);
        Profiler.EndSample();

        Profiler.BeginSample("Set Agent Position");
        Vector3 agentPos = DataHandler.Instance.AgentPosition;
        frustumCullingShader.SetVector(_fAgentPositionId, new Vector4(agentPos.x, agentPos.y, agentPos.z, 0f));
        Profiler.EndSample();

        Profiler.BeginSample("Set HiZ Occlusion");
        // Compute shaders ignore Shader.SetGlobal*, and this is an Instantiate()d copy —
        // so the Hi-Z state has to be pushed here, per instancer, per dispatch.
        HiZOcclusionManager hiz = HiZOcclusionManager.Instance;
        bool occlusionOn = hiz != null && hiz.OcclusionEnabled && hiz.IsValid;
        frustumCullingShader.SetInt(_fOcclusionEnabledId, occlusionOn ? 1 : 0);
        if (occlusionOn)
        {
            frustumCullingShader.SetTexture(_fKernelId, _fHizTextureId, hiz.HiZTexture);
            frustumCullingShader.SetInt(_fHizMipCountId, hiz.HiZMipCount);
            frustumCullingShader.SetVector(_fHizTextureSizeId, hiz.HiZTextureSize);
            frustumCullingShader.SetMatrix(_fHizViewProjId, hiz.HiZViewProj);
            frustumCullingShader.SetVector(_fHizProjScaleId, hiz.HiZProjScale);
            frustumCullingShader.SetVector(_fCameraPositionId, hiz.CameraPosition);
            frustumCullingShader.SetFloat(_fDepthBiasId, hiz.DepthBias);
            frustumCullingShader.SetInt(_fHizFlipYId, hiz.FlipY ? 1 : 0);
        }
        else
        {
            frustumCullingShader.SetTexture(_fKernelId, _fHizTextureId, _dummyHiZTexture);
        }
        Profiler.EndSample();

        Profiler.BeginSample("Dispatch Compute Shader");
        for (int i = 0; i < lods.Length; i++)
        {
            _visibleIndexBuffers[i].SetCounterValue(0);
        }

        frustumCullingShader.Dispatch(
            _fKernelId,
            Mathf.CeilToInt(instances.Count / (float)_fThreadGroupSizeX),
            1,
            1
        );
        Profiler.EndSample();

        // Draw each active LOD
        for (int i = 0; i < lods.Length; i++)
        {
            if (_subMeshPerLOD[i] == null || lods[i].mesh == null) continue;
            for (int s = 0; s < _subMeshPerLOD[i].Length; s++)
            {
                _subMeshPerLOD[i][s].material.SetBuffer(_sVisibleIndicesId, _visibleIndexBuffers[i]);
                _subMeshPerLOD[i][s].material.SetBuffer(_sPositionsId, _positionBuffer);
                _subMeshPerLOD[i][s].material.SetBuffer(_sLodTransformDataId, _lodTransformBuffers[i]);
                _subMeshPerLOD[i][s].UpdateInstanceCountBuf(_visibleIndexBuffers[i]);

                Graphics.DrawMeshInstancedIndirect(
                    lods[i].mesh,
                    s,
                    _subMeshPerLOD[i][s].material,
                    new Bounds(Vector3.zero, Vector3.one * 1000f),
                    _subMeshPerLOD[i][s].argsBuffer
                );
            }
        }
    }

    void OnDestroy()
    {
        if (_subMeshPerLOD != null)
            foreach (var lodSubs in _subMeshPerLOD)
                if (lodSubs != null)
                    foreach (var sub in lodSubs) sub.Release();

        _positionBuffer?.Release();
        _simplePlaneBuffer?.Release();
        _dummyBuffer?.Release();

        if (_visibleIndexBuffers != null)
            foreach (var buf in _visibleIndexBuffers) buf?.Release();

        if (_lodTransformBuffers != null)
            foreach (var buf in _lodTransformBuffers) buf?.Release();
    }

    public void RemoveSingleDrawData(InstanceData d)
    {
        instances.Remove(d);
        _buffersDirty = true;
    }

    public void ClearAllDrawData()
    {
        instances = new();
        _buffersDirty = true;
    }

    public void RemoveDrawDataRange(List<InstanceData> toRemove)
    {
        foreach (InstanceData d in toRemove)
            instances.Remove(d);
        _buffersDirty = true;
    }

    public void AddObjectToBatch(InstanceData d)
    {
        instances.Add(d);
        _buffersDirty = true;
    }

    private void RebuildBuffers()
    {
        _positionBuffer?.Release();
        _positionBuffer = new ComputeBuffer(instances.Count, _positionDataSize);

        Vector4[] positions = new Vector4[instances.Count];
        LodRenderData[][] lodTransforms = new LodRenderData[lods.Length][];
        for (int i = 0; i < lods.Length; i++)
            lodTransforms[i] = new LodRenderData[instances.Count];

        // Conservative bounding-sphere base radius for the occlusion test (assumption A5):
        // extents.magnitude covers the box under any rotation, center.magnitude covers an
        // off-pivot bounds center (the sphere stays centered on the pivot we cull with).
        // Max over all LOD meshes so no LOD can poke outside the sphere.
        float baseRadius = 0f;
        for (int i = 0; i < lods.Length; i++)
        {
            if (lods[i].mesh == null) continue;
            Bounds b = lods[i].mesh.bounds;
            baseRadius = Mathf.Max(baseRadius, b.extents.magnitude + b.center.magnitude);
        }

        float minRadius = float.MaxValue, maxRadius = 0f;

        for (int i = 0; i < instances.Count; i++)
        {
            InstanceData instance = instances[i];
            Vector3 scale = instance.lod0.scale;
            float maxScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            float radius = baseRadius * maxScale;
            minRadius = Mathf.Min(minRadius, radius);
            maxRadius = Mathf.Max(maxRadius, radius);
            positions[i] = new Vector4(instance.lod0.position.x, instance.lod0.position.y, instance.lod0.position.z, radius);

            for (int lod = 0; lod < lods.Length; lod++)
            {
                LodTransform t = GetLodTransform(instance, lod);
                lodTransforms[lod][i] = new LodRenderData
                {
                    rotation = t.rotation,
                    scale = new Vector4(t.scale.x, t.scale.y, t.scale.z, 0f)
                };
            }
        }

        _positionBuffer.SetData(positions);

        if (HiZOcclusionManager.LogRadii)
            Debug.Log($"BatchInstancer ({itemId}): {instances.Count} instances, " +
                      $"occlusion sphere radius range [{minRadius:F2}, {maxRadius:F2}] m (A5)");

        if (_lodTransformBuffers != null)
            foreach (var buf in _lodTransformBuffers) buf?.Release();

        _lodTransformBuffers = new ComputeBuffer[lods.Length];
        for (int i = 0; i < lods.Length; i++)
        {
            _lodTransformBuffers[i] = new ComputeBuffer(instances.Count, _lodRenderDataSize);
            _lodTransformBuffers[i].SetData(lodTransforms[i]);
        }

        if (_visibleIndexBuffers != null)
            foreach (var buf in _visibleIndexBuffers) buf?.Release();

        _visibleIndexBuffers = new ComputeBuffer[lods.Length];
        for (int i = 0; i < lods.Length; i++)
        {
            _visibleIndexBuffers[i] = new ComputeBuffer(instances.Count, _visibleIndexSize, ComputeBufferType.Append);
            frustumCullingShader.SetBuffer(_fKernelId, _fLodBufIds[i], _visibleIndexBuffers[i]);
        }

        frustumCullingShader.SetInt(_fNumToDrawId, instances.Count);
        frustumCullingShader.SetBuffer(_fKernelId, _fPositionBufferId, _positionBuffer);

        _buffersDirty = false;
    }

    private static LodTransform GetLodTransform(InstanceData instance, int lod)
    {
        switch (lod)
        {
            case 0: return instance.lod0;
            case 1: return instance.lod1;
            case 2: return instance.lod2;
            case 3: return instance.lod3;
            default: return instance.lod0;
        }
    }
}
