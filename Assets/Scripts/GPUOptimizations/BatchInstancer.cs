using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using JetBrains.Annotations;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

public struct DrawData {
    public Vector3 position;
    public Vector4 rotation;
    public Vector3 scale;
};

// Per-instance data uploaded to the GPU; carries rotation/scale for both LOD levels
// so the compute shader can assemble the correct DrawData for whichever LOD it routes to.
public struct InstanceLODData : IEquatable<InstanceLODData> {
    public Vector3 position;
    public Vector4 rotation0;
    public Vector3 scale0;
    public Vector4 rotation1;
    public Vector3 scale1;

    public bool Equals(InstanceLODData other)
    {
        return position == other.position &&
               rotation0 == other.rotation0 &&
               scale0 == other.scale0 &&
               rotation1 == other.rotation1 &&
               scale1 == other.scale1;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(position, rotation0, scale0, rotation1, scale1);
    }
};

public class BatchInstancer : MonoBehaviour
{
    public Camera agentCamera;
    // LOD1 = high-poly (close), LOD0 = low-poly (far); LOD1 may be null
    public Mesh lodMesh1;
    public Mesh lodMesh0;
    public Material[] materials0;
    public Material[] materials1;
    private SubMeshInstance[] subMeshInstancesLOD1;
    private SubMeshInstance[] subMeshInstancesLOD0;

    public float lodDistance = 3f;

    /* custom shader buffers setup */

    /* holds InstanceLODData for ALL items (of one type) to draw */
    private List<InstanceLODData> instances = new();
    public string itemId;
    private ComputeBuffer _drawDataBuffer;
    private int instanceLODDataSize = Marshal.SizeOf<InstanceLODData>();
    private int drawDataSize = Marshal.SizeOf<DrawData>();

    private int _sDrawDataId;

    /* frustum culling compute shader setup */

    public ComputeShader frustumCullingShader;
    private ComputeBuffer _simplePlaneBuffer;
    private ComputeBuffer _unculledLOD1Buffer;
    private ComputeBuffer _unculledLOD0Buffer;

    private int simplePlaneSize = sizeof(float) * 4;

    private int _fDrawBufferId;
    private int _fNumToDrawId;
    private int _fPlanesId;
    private int _fUnculledLOD1BufferId;
    private int _fUnculledLOD0BufferId;
    private int _fAgentPositionId;
    private int _fLodDistanceId;
    private int _fHasLOD1Id;
    private int _fHasLOD0Id;
    private int _fKernelId;

    private bool ready = false;

    public void Init()
    {
        if (lodMesh1 != null && materials1 != null)
        {
            int count1 = Mathf.Min(materials1.Length, lodMesh1.subMeshCount);
            subMeshInstancesLOD1 = new SubMeshInstance[count1];
            for (int i = 0; i < count1; i++)
            {
                subMeshInstancesLOD1[i] = new SubMeshInstance(
                    lodMesh1.GetIndexCount(i),
                    lodMesh1.GetIndexStart(i),
                    lodMesh1.GetBaseVertex(i),
                    materials1[i]
                );
            }
        }

        if (lodMesh0 != null && materials0 != null)
        {
            int count0 = Mathf.Min(materials0.Length, lodMesh0.subMeshCount);
            subMeshInstancesLOD0 = new SubMeshInstance[count0];
            for (int i = 0; i < count0; i++)
            {
                subMeshInstancesLOD0[i] = new SubMeshInstance(
                    lodMesh0.GetIndexCount(i),
                    lodMesh0.GetIndexStart(i),
                    lodMesh0.GetBaseVertex(i),
                    materials0[i]
                );
            }
        }

        _simplePlaneBuffer = new ComputeBuffer(6, simplePlaneSize);

        _fDrawBufferId         = Shader.PropertyToID("draw_buffer");
        _fNumToDrawId          = Shader.PropertyToID("num_to_draw");
        _fPlanesId             = Shader.PropertyToID("planes");
        _fUnculledLOD1BufferId = Shader.PropertyToID("unculled_lod1_buf");
        _fUnculledLOD0BufferId = Shader.PropertyToID("unculled_lod0_buf");
        _fAgentPositionId      = Shader.PropertyToID("agent_position");
        _fLodDistanceId        = Shader.PropertyToID("lod_distance");
        _fHasLOD1Id            = Shader.PropertyToID("has_lod1");
        _fHasLOD0Id            = Shader.PropertyToID("has_lod0");
        _fKernelId             = frustumCullingShader.FindKernel("CSMain");

        _sDrawDataId = Shader.PropertyToID("_DrawData");

        frustumCullingShader.SetBuffer(_fKernelId, _fPlanesId, _simplePlaneBuffer);
        frustumCullingShader.SetFloat(_fLodDistanceId, lodDistance);
        frustumCullingShader.SetInt(_fHasLOD1Id, lodMesh1 != null ? 1 : 0);
        frustumCullingShader.SetInt(_fHasLOD0Id, lodMesh0 != null ? 1 : 0);

        ready = true;
    }

    // LateUpdate because GPUInstanceTracker has to
    // calculate cameraFrustumPlanes first at Update()
    void Update()
    {
        // Continuously render batches only when Init() has been called at least once
        if (!ready || instances.Count == 0) return;

        Profiler.BeginSample("Get Planes");
        SimplePlane[] planes = GPUInstanceTracker.Instance.cameraFrustumPlanes;
        _simplePlaneBuffer.SetData(planes);
        Profiler.EndSample();

        Profiler.BeginSample("Set Agent Position");
        Vector3 agentPos = DataHandler.Instance.AgentPosition;
        frustumCullingShader.SetVector(_fAgentPositionId, new Vector4(agentPos.x, agentPos.y, agentPos.z, 0f));
        Profiler.EndSample();

        Profiler.BeginSample("Dispatch ComputeShader");
        
        _unculledLOD1Buffer.SetData(Array.Empty<DrawData>());
        _unculledLOD1Buffer.SetCounterValue(0);
        
        _unculledLOD0Buffer.SetData(Array.Empty<DrawData>());
        _unculledLOD0Buffer.SetCounterValue(0);

        frustumCullingShader.Dispatch(
            _fKernelId,
            Mathf.CeilToInt(instances.Count / 64f),
            1,
            1
        );
        Profiler.EndSample();

        // Draw LOD1 (high-poly, close)
        if (subMeshInstancesLOD1 != null && lodMesh1 != null)
        {
            for (int i = 0; i < subMeshInstancesLOD1.Length; i++)
            {
                subMeshInstancesLOD1[i].material.SetBuffer(_sDrawDataId, _unculledLOD1Buffer);
                subMeshInstancesLOD1[i].UpdateInstanceCountBuf(_unculledLOD1Buffer);

                Graphics.DrawMeshInstancedIndirect(
                    lodMesh1,
                    i,
                    subMeshInstancesLOD1[i].material,
                    new Bounds(Vector3.zero, Vector3.one * 1000f),
                    subMeshInstancesLOD1[i].argsBuffer
                );
            }
        }

        // Draw LOD0 (low-poly, far)
        if (subMeshInstancesLOD0 != null && lodMesh0 != null)
        {
            for (int i = 0; i < subMeshInstancesLOD0.Length; i++)
            {
                subMeshInstancesLOD0[i].material.SetBuffer(_sDrawDataId, _unculledLOD0Buffer);
                subMeshInstancesLOD0[i].UpdateInstanceCountBuf(_unculledLOD0Buffer);

                Graphics.DrawMeshInstancedIndirect(
                    lodMesh0,
                    i,
                    subMeshInstancesLOD0[i].material,
                    new Bounds(Vector3.zero, Vector3.one * 1000f),
                    subMeshInstancesLOD0[i].argsBuffer
                );
            }
        }
    }

    void OnDestroy()
    {
        /*
         * Destroy ComputeBuffers
         * C# doesn't handle the cleanup of these
         * since they are in the GPU
         */
        if (subMeshInstancesLOD1 != null)
            foreach (var t in subMeshInstancesLOD1) t.Release();
        if (subMeshInstancesLOD0 != null)
            foreach (var t in subMeshInstancesLOD0) t.Release();

        _drawDataBuffer?.Release();
        _simplePlaneBuffer?.Release();
        _unculledLOD1Buffer?.Release();
        _unculledLOD0Buffer?.Release();
    }

    public void RemoveSingleDrawData(InstanceLODData d)
    {
        instances.Remove(d);
        _drawDataBuffer.SetData(instances);
    }

    public void ClearAllDrawData()
    {
        instances = new();
        _drawDataBuffer.SetData(instances);
    }

    public void RemoveDrawDataRange(List<InstanceLODData> toRemove)
    {
        foreach (InstanceLODData d in toRemove)
            instances.Remove(d);
        _drawDataBuffer.SetData(instances);
    }

    public void AddObjectToBatch(InstanceLODData d)
    {
        instances.Add(d);

        // TODO: Might have a better solution but it works for now
        _drawDataBuffer?.Release();
        _drawDataBuffer = new ComputeBuffer(instances.Count, instanceLODDataSize);
        _drawDataBuffer.SetData(instances);

        _unculledLOD1Buffer?.Release();
        _unculledLOD1Buffer = new ComputeBuffer(instances.Count, drawDataSize, ComputeBufferType.Append);

        _unculledLOD0Buffer?.Release();
        _unculledLOD0Buffer = new ComputeBuffer(instances.Count, drawDataSize, ComputeBufferType.Append);

        if (frustumCullingShader is null)
        {
            Debug.LogWarning("frustumCullingShader is null");
            return;
        }

        frustumCullingShader.SetInt(_fNumToDrawId, instances.Count);
        frustumCullingShader.SetBuffer(_fKernelId, _fDrawBufferId, _drawDataBuffer);
        frustumCullingShader.SetBuffer(_fKernelId, _fUnculledLOD1BufferId, _unculledLOD1Buffer);
        frustumCullingShader.SetBuffer(_fKernelId, _fUnculledLOD0BufferId, _unculledLOD0Buffer);
    }
}
