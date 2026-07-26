using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

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
    private ComputeBuffer[] _lidarVisibleIndexBuffers;
    private ComputeBuffer _dummyBuffer;
    private SubMeshInstance[][] _subMeshPerLOD;

    private readonly int _simplePlaneSize = sizeof(float) * 4;

    private int _fPositionBufferId;
    private int _fNumToDrawId;
    private int _fPlanesId;
    private int _fLodDistancesId;
    private int _fNumLodsId;
    private int _fAgentPositionId;
    private int _fCullingModeId;
    private int _fRangeCullOriginId;
    private int _fRangeCullMaxDistanceSqId;
    private int _fKernelId;
    private uint _fThreadGroupSizeX;

    // Matches the 4 named AppendStructuredBuffer outputs in the compute shader
    private static readonly string[] LodBufNames = { "lod0_buf", "lod1_buf", "lod2_buf", "lod3_buf" };
    private readonly int[] _fLodBufIds = new int[LodBufNames.Length];

    private const int MAX_LODS = 4;

    private bool _ready = false;
    private bool _buffersDirty = false;
    private Bounds _cullingBounds;
    private Bounds _drawBounds;
    private uint _lastMainCullVersion = uint.MaxValue;
    private bool _hasMainCullResults;
    private int _visibleMainLodMask;

    public int InstanceCount => instances.Count;

    public string GetPositionDiagnosticSummary()
    {
        if (instances.Count == 0)
            return $"{itemId}: no instances";

        float minLod0Y = float.PositiveInfinity;
        float maxLod0Y = float.NegativeInfinity;
        float minLod1DeltaY = float.PositiveInfinity;
        float maxLod1DeltaY = float.NegativeInfinity;
        for (int i = 0; i < instances.Count; i++)
        {
            InstanceData instance = instances[i];
            minLod0Y = Mathf.Min(minLod0Y, instance.lod0.position.y);
            maxLod0Y = Mathf.Max(maxLod0Y, instance.lod0.position.y);
            float lod1DeltaY = instance.lod1.position.y - instance.lod0.position.y;
            minLod1DeltaY = Mathf.Min(minLod1DeltaY, lod1DeltaY);
            maxLod1DeltaY = Mathf.Max(maxLod1DeltaY, lod1DeltaY);
        }

        return
            $"{itemId}: instances={instances.Count}, " +
            $"lod0Y={minLod0Y:F3}..{maxLod0Y:F3}, " +
            $"lod1MinusLod0Y={minLod1DeltaY:F3}..{maxLod1DeltaY:F3}";
    }

    public int IndirectDrawCommandCount
    {
        get
        {
            if (_subMeshPerLOD == null) return 0;

            int count = 0;
            for (int i = 0; i < _subMeshPerLOD.Length; i++)
                count += _subMeshPerLOD[i]?.Length ?? 0;
            return count;
        }
    }

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
        _fCullingModeId   = Shader.PropertyToID("culling_mode");
        _fRangeCullOriginId = Shader.PropertyToID("range_cull_origin");
        _fRangeCullMaxDistanceSqId = Shader.PropertyToID("range_cull_max_distance_sq");
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
        {
            RebuildBuffers();
            _hasMainCullResults = false;
        }

        if (instances.Count == 0) return;
        if (!IntersectsMainCameraFrustum()) return;

        GPUInstanceTracker tracker = GPUInstanceTracker.Instance;
        uint cullingVersion = tracker != null ? tracker.MainCameraCullingVersion : 0;
        bool needsCull = !_hasMainCullResults || _lastMainCullVersion != cullingVersion;
        if (needsCull)
        {
            CullForMainCamera();
            _lastMainCullVersion = cullingVersion;
            _hasMainCullResults = true;
        }

        DrawVisibleBuffers(_visibleIndexBuffers, needsCull, _visibleMainLodMask);
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

        if (_lidarVisibleIndexBuffers != null)
            foreach (var buf in _lidarVisibleIndexBuffers) buf?.Release();

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

        RecalculateDrawBounds();
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

        if (_lidarVisibleIndexBuffers != null)
            foreach (var buf in _lidarVisibleIndexBuffers) buf?.Release();

        _lidarVisibleIndexBuffers = new ComputeBuffer[lods.Length];
        for (int i = 0; i < lods.Length; i++)
            _lidarVisibleIndexBuffers[i] = new ComputeBuffer(instances.Count, _visibleIndexSize, ComputeBufferType.Append);

        frustumCullingShader.SetInt(_fNumToDrawId, instances.Count);
        frustumCullingShader.SetBuffer(_fKernelId, _fPositionBufferId, _positionBuffer);

        _buffersDirty = false;
    }

    public void CullForLidarRange(Vector3 origin, float maxRange)
    {
        if (!_ready || instances.Count == 0) return;

        if (_buffersDirty)
            RebuildBuffers();

        DispatchCulling(
            _lidarVisibleIndexBuffers,
            true,
            null,
            origin,
            Mathf.Max(0.01f, maxRange));
    }

    public void AddLidarDrawCommands(CommandBuffer cmd)
    {
        if (!_ready || instances.Count == 0 || _lidarVisibleIndexBuffers == null) return;

        AddDrawCommands(cmd, _lidarVisibleIndexBuffers);
    }

    public LidarIndirectDrawStats AddLidarDepthDrawCommands(CommandBuffer cmd, Material depthMaterial)
    {
        LidarIndirectDrawStats stats = new LidarIndirectDrawStats
        {
            batchers = 1,
            sourceInstances = instances.Count
        };

        if (!_ready || depthMaterial == null)
        {
            stats.skippedNotReady = 1;
            return stats;
        }

        stats.readyBatchers = 1;

        if (instances.Count == 0)
        {
            stats.skippedNoInstances = 1;
            return stats;
        }

        if (_lidarVisibleIndexBuffers == null)
        {
            stats.skippedMissingBuffers = 1;
            return stats;
        }

        AddDepthDrawCommands(cmd, _lidarVisibleIndexBuffers, depthMaterial, ref stats);
        return stats;
    }

    private void CullForMainCamera()
    {
        SimplePlane[] planes = GPUInstanceTracker.Instance.cameraFrustumPlanes;
        if (planes == null) return;

        Vector3 agentPos =
            DataHandler.Instance != null
                ? DataHandler.Instance.AgentPosition
                : (agentCamera != null ? agentCamera.transform.position : transform.position);

        _visibleMainLodMask = CalculateVisibleLodMask(planes, agentPos);
        DispatchCulling(_visibleIndexBuffers, false, planes, agentPos, 0f);
    }

    private void DispatchCulling(
        ComputeBuffer[] targetBuffers,
        bool rangeMode,
        SimplePlane[] planes,
        Vector3 cullOrigin,
        float maxRange)
    {
        if (targetBuffers == null || targetBuffers.Length == 0) return;

        Profiler.BeginSample(rangeMode ? "LiDAR Range Culling" : "Frustum Culling");

        for (int i = 0; i < lods.Length; i++)
        {
            targetBuffers[i].SetCounterValue(0);
            frustumCullingShader.SetBuffer(_fKernelId, _fLodBufIds[i], targetBuffers[i]);
        }

        if (!rangeMode && planes != null)
            _simplePlaneBuffer.SetData(planes);

        frustumCullingShader.SetInt(_fCullingModeId, rangeMode ? 1 : 0);
        frustumCullingShader.SetVector(_fAgentPositionId, new Vector4(cullOrigin.x, cullOrigin.y, cullOrigin.z, 0f));
        frustumCullingShader.SetVector(_fRangeCullOriginId, new Vector4(cullOrigin.x, cullOrigin.y, cullOrigin.z, 0f));
        frustumCullingShader.SetFloat(_fRangeCullMaxDistanceSqId, maxRange * maxRange);

        frustumCullingShader.Dispatch(
            _fKernelId,
            Mathf.CeilToInt(instances.Count / (float)_fThreadGroupSizeX),
            1,
            1
        );

        Profiler.EndSample();
    }

    private void DrawVisibleBuffers(
        ComputeBuffer[] visibleBuffers,
        bool updateInstanceCounts,
        int visibleLodMask)
    {
        for (int i = 0; i < lods.Length; i++)
        {
            if ((visibleLodMask & (1 << i)) == 0) continue;
            if (_subMeshPerLOD[i] == null || lods[i].mesh == null) continue;
            for (int s = 0; s < _subMeshPerLOD[i].Length; s++)
            {
                SubMeshInstance subMesh = _subMeshPerLOD[i][s];
                BindMaterialBuffers(subMesh.material, visibleBuffers[i], i);
                if (updateInstanceCounts)
                    subMesh.UpdateInstanceCountBuf(visibleBuffers[i]);

                Graphics.DrawMeshInstancedIndirect(
                    lods[i].mesh,
                    s,
                    subMesh.material,
                    _drawBounds,
                    subMesh.argsBuffer,
                    0,
                    null,
                    ShadowCastingMode.Off,
                    true
                );
            }
        }
    }

    private int CalculateVisibleLodMask(SimplePlane[] planes, Vector3 agentPos)
    {
        int mask = 0;
        float hardCullDistanceSq =
            lods[lods.Length - 1].maxDistance * lods[lods.Length - 1].maxDistance;

        for (int instanceIndex = 0; instanceIndex < instances.Count; instanceIndex++)
        {
            Vector3 cullPosition = instances[instanceIndex].lod0.position;
            bool insideFrustum = true;
            for (int planeIndex = 0; planeIndex < 6; planeIndex++)
            {
                SimplePlane plane = planes[planeIndex];
                if (Vector3.Dot(plane.normal, cullPosition) + plane.distance < -0.2f)
                {
                    insideFrustum = false;
                    break;
                }
            }

            if (!insideFrustum)
                continue;

            float distanceSq = (cullPosition - agentPos).sqrMagnitude;
            if (distanceSq >= hardCullDistanceSq)
                continue;

            int lodIndex = lods.Length - 1;
            for (int i = 0; i < lods.Length - 1; i++)
            {
                float threshold = lods[i].maxDistance;
                if (distanceSq < threshold * threshold)
                {
                    lodIndex = i;
                    break;
                }
            }

            mask |= 1 << lodIndex;
            if (mask == (1 << lods.Length) - 1)
                break;
        }

        return mask;
    }

    private void AddDrawCommands(CommandBuffer cmd, ComputeBuffer[] visibleBuffers)
    {
        for (int i = 0; i < lods.Length; i++)
        {
            if (_subMeshPerLOD[i] == null || lods[i].mesh == null) continue;
            for (int s = 0; s < _subMeshPerLOD[i].Length; s++)
            {
                SubMeshInstance subMesh = _subMeshPerLOD[i][s];
                BindMaterialBuffers(subMesh.material, visibleBuffers[i], i);
                subMesh.UpdateInstanceCountBuf(visibleBuffers[i]);
                cmd.DrawMeshInstancedIndirect(lods[i].mesh, s, subMesh.material, 0, subMesh.argsBuffer);
            }
        }
    }

    private void AddDepthDrawCommands(
        CommandBuffer cmd,
        ComputeBuffer[] visibleBuffers,
        Material depthMaterial,
        ref LidarIndirectDrawStats stats)
    {
        for (int i = 0; i < lods.Length; i++)
        {
            if (_subMeshPerLOD[i] == null || lods[i].mesh == null) continue;
            stats.lods++;

            for (int s = 0; s < _subMeshPerLOD[i].Length; s++)
            {
                SubMeshInstance subMesh = _subMeshPerLOD[i][s];
                subMesh.UpdateInstanceCountBuf(visibleBuffers[i]);

                MaterialPropertyBlock properties = new MaterialPropertyBlock();
                properties.SetBuffer(_sVisibleIndicesId, visibleBuffers[i]);
                properties.SetBuffer(_sPositionsId, _positionBuffer);
                properties.SetBuffer(_sLodTransformDataId, _lodTransformBuffers[i]);
                cmd.DrawMeshInstancedIndirect(lods[i].mesh, s, depthMaterial, 0, subMesh.argsBuffer, 0, properties);
                stats.submeshes++;
                stats.queuedCommands++;
            }
        }
    }

    private void BindMaterialBuffers(Material material, ComputeBuffer visibleBuffer, int lodIndex)
    {
        material.SetBuffer(_sVisibleIndicesId, visibleBuffer);
        material.SetBuffer(_sPositionsId, _positionBuffer);
        material.SetBuffer(_sLodTransformDataId, _lodTransformBuffers[lodIndex]);
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

    private bool IntersectsMainCameraFrustum()
    {
        SimplePlane[] planes = GPUInstanceTracker.Instance?.cameraFrustumPlanes;
        if (planes == null || planes.Length < 6)
            return false;

        Vector3 min = _cullingBounds.min;
        Vector3 max = _cullingBounds.max;

        for (int i = 0; i < 6; i++)
        {
            SimplePlane plane = planes[i];
            Vector3 positiveVertex = new Vector3(
                plane.normal.x >= 0f ? max.x : min.x,
                plane.normal.y >= 0f ? max.y : min.y,
                plane.normal.z >= 0f ? max.z : min.z);

            if (Vector3.Dot(plane.normal, positiveVertex) + plane.distance < 0f)
                return false;
        }

        return true;
    }

    private void RecalculateDrawBounds()
    {
        bool initialized = false;
        Bounds combined = default;

        for (int instanceIndex = 0; instanceIndex < instances.Count; instanceIndex++)
        {
            InstanceData instance = instances[instanceIndex];
            for (int lodIndex = 0; lodIndex < lods.Length; lodIndex++)
            {
                Mesh mesh = lods[lodIndex].mesh;
                if (mesh == null) continue;

                LodTransform transformData = GetLodTransform(instance, lodIndex);
                Quaternion rotation = new Quaternion(
                    transformData.rotation.x,
                    transformData.rotation.y,
                    transformData.rotation.z,
                    transformData.rotation.w);
                Vector3 absoluteScale = new Vector3(
                    Mathf.Abs(transformData.scale.x),
                    Mathf.Abs(transformData.scale.y),
                    Mathf.Abs(transformData.scale.z));
                Vector3 center = transformData.position +
                                 rotation * Vector3.Scale(mesh.bounds.center, transformData.scale);
                float radius = Vector3.Scale(mesh.bounds.extents, absoluteScale).magnitude;
                Bounds instanceBounds = new Bounds(center, Vector3.one * (radius * 2f));

                if (!initialized)
                {
                    combined = instanceBounds;
                    initialized = true;
                }
                else
                {
                    combined.Encapsulate(instanceBounds);
                }
            }
        }

        _cullingBounds = initialized
            ? combined
            : new Bounds(transform.position, Vector3.one);
        _cullingBounds.Expand(0.4f);

        // The procedural shader multiplies its TRS by Unity's indirect-draw object matrix.
        // Keep the bounds passed to Graphics centered at the world origin so that matrix stays
        // identity; a non-zero bounds center translates an entire product batch a second time.
        Vector3 min = _cullingBounds.min;
        Vector3 max = _cullingBounds.max;
        float originRadius = Mathf.Max(
            Mathf.Abs(min.x), Mathf.Abs(max.x),
            Mathf.Abs(min.y), Mathf.Abs(max.y),
            Mathf.Abs(min.z), Mathf.Abs(max.z));
        _drawBounds = new Bounds(Vector3.zero, Vector3.one * (originRadius * 2f + 0.4f));
    }
}
