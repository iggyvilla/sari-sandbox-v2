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

    // Matches the 4 named AppendStructuredBuffer outputs in the compute shader
    private static readonly string[] LodBufNames = { "lod0_buf", "lod1_buf", "lod2_buf", "lod3_buf" };
    private readonly int[] _fLodBufIds = new int[LodBufNames.Length];

    private const int MAX_LODS = 4;
    private const int THREAD_GROUP_SIZE = 128;

    private bool _ready = false;
    private bool _buffersDirty = false;

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
        for (int i = 0; i < LodBufNames.Length; i++)
            _fLodBufIds[i] = Shader.PropertyToID(LodBufNames[i]);

        _fKernelId = frustumCullingShader.FindKernel("CSMain");
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

        Profiler.BeginSample("Dispatch Compute Shader");
        for (int i = 0; i < lods.Length; i++)
        {
            _visibleIndexBuffers[i].SetCounterValue(0);
        }

        frustumCullingShader.Dispatch(
            _fKernelId,
            Mathf.CeilToInt(instances.Count / (float)THREAD_GROUP_SIZE),
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

        for (int i = 0; i < instances.Count; i++)
        {
            InstanceData instance = instances[i];
            positions[i] = new Vector4(instance.lod0.position.x, instance.lod0.position.y, instance.lod0.position.z, 0f);

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
