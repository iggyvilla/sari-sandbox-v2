using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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

// Every GPU instance of one product (or combined row chunk): instance data, draw buffers, and indirect draws.
// Culling for all batches runs in InstanceCullingSystem.
public class BatchInstancer : MonoBehaviour
{
    public const int MaxLods = 4;

    // CPU mirror of the GPU test is made slightly more permissive so it never drops a LOD the GPU fills.
    private const float CpuCullEpsilon = 1e-3f;
    private const int ArgsStride = 5;

    public LODDefinition[] lods;
    public string itemId;

    private readonly List<InstanceData> _instances = new();
    private Vector4[] _spheres = Array.Empty<Vector4>();
    private ComputeBuffer _positionBuffer;
    private ComputeBuffer[] _lodTransformBuffers;
    private int[] _argsEntryStart;
    private int[] _subMeshCounts;
    private Vector4 _lodDistancesSq;
    private bool _ready;
    private bool _buffersDirty;
    private Bounds _cullingBounds;
    private Bounds _drawBounds;

    public int InstanceCount => _instances.Count;
    public int IndirectDrawCommandCount { get; private set; }
    public int LodCount => lods.Length;
    public Vector4 LodDistancesSq => _lodDistancesSq;
    public IReadOnlyList<InstanceData> Instances => _instances;
    public IReadOnlyList<Vector4> Spheres => _spheres;
    public ComputeBuffer PositionBuffer => _positionBuffer;
    public ComputeBuffer LodTransformBuffer(int lod) => _lodTransformBuffers[lod];
    public bool IsDrawable => _ready && isActiveAndEnabled && _instances.Count > 0;

    // Bumped whenever instance data changes, so the culling system knows to rebuild its buffers.
    public int DataVersion { get; private set; }

    private float HardCullDistanceSq => _lodDistancesSq[lods.Length - 1];

    public void Init(string id, LODDefinition[] lodDefinitions)
    {
        itemId = id;
        lods = lodDefinitions;
        if (lods == null || lods.Length == 0 || lods.Length > MaxLods)
        {
            Debug.LogError($"BatchInstancer ({itemId}): expected 1-{MaxLods} LODs.");
            return;
        }

        _argsEntryStart = new int[lods.Length];
        _subMeshCounts = new int[lods.Length];
        for (int lod = 0; lod < lods.Length; lod++)
        {
            Mesh mesh = lods[lod].mesh;
            Material[] materials = lods[lod].materials;
            _argsEntryStart[lod] = IndirectDrawCommandCount;
            _subMeshCounts[lod] = mesh != null && materials != null
                ? Mathf.Min(materials.Length, mesh.subMeshCount)
                : 0;
            IndirectDrawCommandCount += _subMeshCounts[lod];
            _lodDistancesSq[lod] = lods[lod].maxDistance * lods[lod].maxDistance;
        }

        _ready = true;
    }

    void OnDestroy() => ReleaseBuffers();

    public void AddObjectToBatch(InstanceData d)
    {
        _instances.Add(d);
        MarkDirty();
    }

    public void RemoveSingleDrawData(InstanceData d)
    {
        if (_instances.Remove(d))
            MarkDirty();
    }

    // Bulk removal: one pass instead of a linear search per instance.
    public void RemoveDrawData(HashSet<InstanceData> toRemove)
    {
        if (_instances.RemoveAll(toRemove.Contains) > 0)
            MarkDirty();
    }

    public void ClearAllDrawData()
    {
        _instances.Clear();
        MarkDirty();
    }

    private void MarkDirty()
    {
        _buffersDirty = true;
        DataVersion++;
    }

    // Appends this batch's indirect args (index count, 0 instances, start index, base vertex, 0) per LOD/submesh.
    public void AppendArgsTemplate(List<uint> args, List<uint> counterSources, int batchIndex)
    {
        for (int lod = 0; lod < lods.Length; lod++)
        {
            for (int s = 0; s < _subMeshCounts[lod]; s++)
            {
                Mesh mesh = lods[lod].mesh;
                args.Add(mesh.GetIndexCount(s));
                args.Add(0);
                args.Add(mesh.GetIndexStart(s));
                args.Add(mesh.GetBaseVertex(s));
                args.Add(0);
                counterSources.Add((uint)(batchIndex * MaxLods + lod));
            }
        }
    }

    // Draws each LOD in lodMask for one camera, reading instance counts from the viewer's args buffer.
    public void Draw(Camera cam, ComputeBuffer args, int argsStart, MaterialPropertyBlock[] props, int lodMask)
    {
        for (int lod = 0; lod < lods.Length; lod++)
        {
            if ((lodMask & (1 << lod)) == 0) continue;
            for (int s = 0; s < _subMeshCounts[lod]; s++)
            {
                Graphics.DrawMeshInstancedIndirect(
                    lods[lod].mesh,
                    s,
                    lods[lod].materials[s],
                    _drawBounds,
                    args,
                    ArgsOffset(argsStart, lod, s),
                    props[lod],
                    ShadowCastingMode.Off,
                    true,
                    0,
                    cam);
            }
        }
    }

    public LidarIndirectDrawStats AddLidarDepthDrawCommands(
        CommandBuffer cmd, Material depthMaterial, ComputeBuffer args, int argsStart, MaterialPropertyBlock[] props)
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

        if (args == null || props == null)
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
                    lods[lod].mesh, s, depthMaterial, 0, args, ArgsOffset(argsStart, lod, s), props[lod]);
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

    // Rebuilds this batch's draw buffers and bounding spheres after instance changes.
    public void PrepareBuffers()
    {
        if (!_ready || !_buffersDirty) return;
        _buffersDirty = false;
        ReleaseBuffers();

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
            positions[i] = instance.lod0.position;
            _spheres[i] = CalculateBoundingSphere(instance);

            for (int lod = 0; lod < lods.Length; lod++)
            {
                LodTransform t = instance[lod];
                lodRenderData[lod][i] = new LodRenderData { rotation = t.rotation, scale = t.scale };
            }
        }

        _positionBuffer = new ComputeBuffer(count, sizeof(float) * 4);
        _positionBuffer.SetData(positions);

        _lodTransformBuffers = new ComputeBuffer[lods.Length];
        for (int lod = 0; lod < lods.Length; lod++)
        {
            _lodTransformBuffers[lod] = new ComputeBuffer(count, Marshal.SizeOf<LodRenderData>());
            _lodTransformBuffers[lod].SetData(lodRenderData[lod]);
        }

        RecalculateBounds();
    }

    private void ReleaseBuffers()
    {
        _positionBuffer?.Release();
        if (_lodTransformBuffers != null)
            foreach (ComputeBuffer buffer in _lodTransformBuffers) buffer?.Release();
        _positionBuffer = null;
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
    public bool BoundsVisible(in CullView view)
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

    // CPU pass that mirrors the GPU frustum/distance test to find which LODs have any visible instance,
    // so empty LODs cost no draw call.
    public int CalculateVisibleLodMask(in CullView view)
    {
        int fullMask = (1 << lods.Length) - 1;
        if (view.IsRange) return fullMask;

        int mask = 0;
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

    private int ArgsOffset(int argsStart, int lod, int subMesh) =>
        (argsStart + _argsEntryStart[lod] + subMesh) * ArgsStride * sizeof(uint);
}
