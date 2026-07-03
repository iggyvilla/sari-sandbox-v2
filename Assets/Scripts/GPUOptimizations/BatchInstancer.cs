using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Profiling;
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

public struct BatchInstancerDebugStats
{
    public string itemId;
    public int sourceInstances;
    public int lods;
    public int submeshDraws;
    public int materialSlots;
    public int textureSlots;
    public int visibleInstances;
    public long estimatedSourceVerticesLod0;
    public long estimatedSourceTrianglesLod0;
    public long estimatedVisibleVertices;
    public long estimatedVisibleTriangles;
}

public class BatchInstancer : MonoBehaviour
{
    public Camera agentCamera;

    // LOD table: lods[0] = _LOD0 (highest quality, closest), lods[N-1] = farthest.
    // Set by GPUInstanceTracker.PrepareBatchInstancer, or configured directly in the Inspector.
    public LODDefinition[] lods;
    public bool ownsLodMaterials;
    public bool ownsLodMeshes;

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

    private static readonly ProfilerMarker RebuildBuffersMarker = new("Sari.BatchInstancer.RebuildBuffers");
    private static readonly ProfilerMarker FrustumCullingMarker = new("Sari.BatchInstancer.FrustumCulling");
    private static readonly ProfilerMarker DrawVisibleBuffersMarker = new("Sari.BatchInstancer.DrawVisibleBuffers");
    private static readonly ProfilerMarker DrawSubmeshMarker = new("Sari.BatchInstancer.DrawSubmesh");

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
            RebuildBuffers();

        if (instances.Count == 0) return;

        CullForMainCamera();
        DrawVisibleBuffers(_visibleIndexBuffers);
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

        ReleaseOwnedLodAssets();
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
        using (RebuildBuffersMarker.Auto())
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

            if (_lidarVisibleIndexBuffers != null)
                foreach (var buf in _lidarVisibleIndexBuffers) buf?.Release();

            _lidarVisibleIndexBuffers = new ComputeBuffer[lods.Length];
            for (int i = 0; i < lods.Length; i++)
                _lidarVisibleIndexBuffers[i] = new ComputeBuffer(instances.Count, _visibleIndexSize, ComputeBufferType.Append);

            frustumCullingShader.SetInt(_fNumToDrawId, instances.Count);
            frustumCullingShader.SetBuffer(_fKernelId, _fPositionBufferId, _positionBuffer);

            _buffersDirty = false;
        }
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

        using (FrustumCullingMarker.Auto())
        {
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
    }

    private void DrawVisibleBuffers(ComputeBuffer[] visibleBuffers)
    {
        using (DrawVisibleBuffersMarker.Auto())
        {
            for (int i = 0; i < lods.Length; i++)
            {
                if (_subMeshPerLOD[i] == null || lods[i].mesh == null) continue;
                for (int s = 0; s < _subMeshPerLOD[i].Length; s++)
                {
                    using (DrawSubmeshMarker.Auto())
                    {
                        SubMeshInstance subMesh = _subMeshPerLOD[i][s];
                        BindMaterialBuffers(subMesh.material, visibleBuffers[i], i);
                        subMesh.UpdateInstanceCountBuf(visibleBuffers[i]);

                        Graphics.DrawMeshInstancedIndirect(
                            lods[i].mesh,
                            s,
                            subMesh.material,
                            new Bounds(Vector3.zero, Vector3.one * 1000f),
                            subMesh.argsBuffer
                        );
                    }
                }
            }
        }
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

    public BatchInstancerDebugStats GetDebugStats(bool captureVisibleCounts)
    {
        BatchInstancerDebugStats stats = new()
        {
            itemId = itemId,
            sourceInstances = instances.Count,
            lods = lods != null ? lods.Length : 0
        };

        if (lods == null)
            return stats;

        HashSet<Texture> textures = new();
        for (int i = 0; i < lods.Length; i++)
        {
            Mesh mesh = lods[i].mesh;
            Material[] materials = lods[i].materials;
            int submeshCount = _subMeshPerLOD != null && i < _subMeshPerLOD.Length && _subMeshPerLOD[i] != null
                ? _subMeshPerLOD[i].Length
                : 0;

            stats.submeshDraws += submeshCount;
            if (materials != null)
            {
                stats.materialSlots += materials.Length;
                AddMaterialTextures(materials, textures);
            }

            if (mesh == null)
                continue;

            long trianglesPerInstance = GetTriangleCount(mesh);
            if (i == 0)
            {
                stats.estimatedSourceVerticesLod0 += (long)mesh.vertexCount * instances.Count;
                stats.estimatedSourceTrianglesLod0 += trianglesPerInstance * instances.Count;
            }

            if (!captureVisibleCounts)
                continue;

            int visible = CaptureVisibleCountForLod(i);
            stats.visibleInstances += visible;
            stats.estimatedVisibleVertices += (long)mesh.vertexCount * visible;
            stats.estimatedVisibleTriangles += trianglesPerInstance * visible;
        }

        stats.textureSlots = textures.Count;
        return stats;
    }

    private int CaptureVisibleCountForLod(int lodIndex)
    {
        if (_subMeshPerLOD == null ||
            lodIndex < 0 ||
            lodIndex >= _subMeshPerLOD.Length ||
            _subMeshPerLOD[lodIndex] == null ||
            _subMeshPerLOD[lodIndex].Length == 0 ||
            _subMeshPerLOD[lodIndex][0]?.argsBuffer == null)
        {
            return 0;
        }

        uint[] argsData = new uint[5];
        _subMeshPerLOD[lodIndex][0].argsBuffer.GetData(argsData);
        return (int)argsData[1];
    }

    private static long GetTriangleCount(Mesh mesh)
    {
        long indices = 0;
        for (int i = 0; i < mesh.subMeshCount; i++)
            indices += (long)mesh.GetIndexCount(i);
        return indices / 3L;
    }

    private static void AddMaterialTextures(Material[] materials, HashSet<Texture> textures)
    {
        for (int i = 0; i < materials.Length; i++)
        {
            Material material = materials[i];
            if (material == null)
                continue;

            AddTextureIfPresent(material, "_BaseMap", textures);
            AddTextureIfPresent(material, "_MainTex", textures);
            AddTextureIfPresent(material, "_BumpMap", textures);
            AddTextureIfPresent(material, "_EmissionMap", textures);
            AddTextureIfPresent(material, "_MetallicGlossMap", textures);
            AddTextureIfPresent(material, "_OcclusionMap", textures);
        }
    }

    private static void AddTextureIfPresent(Material material, string propertyName, HashSet<Texture> textures)
    {
        if (!material.HasProperty(propertyName))
            return;

        Texture texture = material.GetTexture(propertyName);
        if (texture != null)
            textures.Add(texture);
    }

    private void ReleaseOwnedLodAssets()
    {
        if (lods == null)
            return;

        if (ownsLodMaterials)
        {
            for (int i = 0; i < lods.Length; i++)
            {
                Material[] materials = lods[i].materials;
                if (materials == null)
                    continue;

                for (int j = 0; j < materials.Length; j++)
                {
                    if (materials[j] != null)
                        Destroy(materials[j]);
                }
            }
        }

        if (ownsLodMeshes)
        {
            for (int i = 0; i < lods.Length; i++)
            {
                if (lods[i].mesh != null)
                    Destroy(lods[i].mesh);
            }
        }
    }
}
