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

// Per-instance data for the GPU: one independent transform per LOD level.
// lods[0] = _LOD0 (always present — base / farthest), higher indices = closer / higher quality.
// Matches HLSL InstanceData { LodTransform lods[4]; } — fields are sequential in memory.
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

public struct DrawData
{
    public Vector3 position;
    public Vector4 rotation;
    public Vector3 scale;
}

// Describes one LOD level: the mesh, its cloned instancing materials, and the distance
// threshold below which we upgrade FROM this LOD to the next higher-quality one.
// upgradeDistance is unused on the last (highest-quality) entry.
[System.Serializable]
public struct LODDefinition
{
    public Mesh mesh;
    public Material[] materials;
    [Tooltip("Upgrade to the next quality level when closer than this distance. Unused on the last LOD.")]
    public float upgradeDistance;
}

public class BatchInstancer : MonoBehaviour
{
    public Camera agentCamera;

    // LOD table: lods[0] = _LOD0 (always present), higher indices = higher quality / closer.
    // Set by GPUInstanceTracker.PrepareBatchInstancer, or configured directly in the Inspector.
    public LODDefinition[] lods;

    private List<InstanceData> instances = new();
    public string itemId;

    private ComputeBuffer _drawDataBuffer;
    private int _instanceDataSize;
    private int _drawDataSize = Marshal.SizeOf<DrawData>();

    private int _sDrawDataId;

    public ComputeShader frustumCullingShader;
    private ComputeBuffer _simplePlaneBuffer;

    // One AppendBuffer per active LOD; _dummyBuffer fills unused named HLSL slots
    private ComputeBuffer[] _unculledLODBuffers;
    private ComputeBuffer _dummyBuffer;
    private SubMeshInstance[][] _subMeshPerLOD;

    private readonly int _simplePlaneSize = sizeof(float) * 4;

    private int _fDrawBufferId;
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

    private bool _ready = false;
    private bool _buffersDirty = false;

    public void Init()
    {
        _instanceDataSize = Marshal.SizeOf<InstanceData>();

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

        _fDrawBufferId    = Shader.PropertyToID("draw_buffer");
        _fNumToDrawId     = Shader.PropertyToID("num_to_draw");
        _fPlanesId        = Shader.PropertyToID("planes");
        _fLodDistancesId  = Shader.PropertyToID("lod_distances");
        _fNumLodsId       = Shader.PropertyToID("num_lods");
        _fAgentPositionId = Shader.PropertyToID("agent_position");
        for (int i = 0; i < LodBufNames.Length; i++)
            _fLodBufIds[i] = Shader.PropertyToID(LodBufNames[i]);

        _fKernelId   = frustumCullingShader.FindKernel("CSMain");
        _sDrawDataId = Shader.PropertyToID("_DrawData");

        // Assemble lod_distances: lods[i].upgradeDistance = threshold to step up to lods[i+1]
        // Stored descending (outer → inner) as a float4
        Vector4 lodDist = Vector4.zero;
        for (int i = 0; i < Mathf.Min(lods.Length - 1, MAX_LODS - 1); i++)
            lodDist[i] = lods[i].upgradeDistance;

        frustumCullingShader.SetBuffer(_fKernelId, _fPlanesId, _simplePlaneBuffer);
        frustumCullingShader.SetVector(_fLodDistancesId, lodDist);
        frustumCullingShader.SetInt(_fNumLodsId, lods.Length);

        // Dummy 1-element buffer for HLSL slots beyond the active LOD count
        _dummyBuffer = new ComputeBuffer(1, _drawDataSize, ComputeBufferType.Append);
        for (int i = lods.Length; i < MAX_LODS; i++)
            frustumCullingShader.SetBuffer(_fKernelId, _fLodBufIds[i], _dummyBuffer);

        _ready = true;
    }

    // LateUpdate because GPUInstanceTracker calculates cameraFrustumPlanes first at Update()
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

        Profiler.BeginSample("Dispatch ComputeShader");
        for (int i = 0; i < lods.Length; i++)
        {
            _unculledLODBuffers[i].SetData(Array.Empty<DrawData>());
            _unculledLODBuffers[i].SetCounterValue(0);
        }

        frustumCullingShader.Dispatch(
            _fKernelId,
            Mathf.CeilToInt(instances.Count / 64f),
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
                _subMeshPerLOD[i][s].material.SetBuffer(_sDrawDataId, _unculledLODBuffers[i]);
                _subMeshPerLOD[i][s].UpdateInstanceCountBuf(_unculledLODBuffers[i]);

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

        _drawDataBuffer?.Release();
        _simplePlaneBuffer?.Release();
        _dummyBuffer?.Release();

        if (_unculledLODBuffers != null)
            foreach (var buf in _unculledLODBuffers) buf?.Release();
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
        _drawDataBuffer?.Release();
        _drawDataBuffer = new ComputeBuffer(instances.Count, _instanceDataSize);
        _drawDataBuffer.SetData(instances);

        if (_unculledLODBuffers != null)
            foreach (var buf in _unculledLODBuffers) buf?.Release();

        _unculledLODBuffers = new ComputeBuffer[lods.Length];
        for (int i = 0; i < lods.Length; i++)
        {
            _unculledLODBuffers[i] = new ComputeBuffer(instances.Count, _drawDataSize, ComputeBufferType.Append);
            frustumCullingShader.SetBuffer(_fKernelId, _fLodBufIds[i], _unculledLODBuffers[i]);
        }

        frustumCullingShader.SetInt(_fNumToDrawId, instances.Count);
        frustumCullingShader.SetBuffer(_fKernelId, _fDrawBufferId, _drawDataBuffer);

        _buffersDirty = false;
    }
}
