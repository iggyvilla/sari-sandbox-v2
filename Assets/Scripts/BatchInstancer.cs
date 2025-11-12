using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

struct SimplePlane
{
    public float distance;
    public Vector3 normal;
}

public struct DrawData {
    public Vector3 position;
    public Vector4 rotation;
    public Vector3 scale;
};

public class BatchInstancer : MonoBehaviour
{
    public Camera agentCamera;
    public Mesh mesh;
    public Material[] materials;
    public ComputeShader frustumCullingShader;
    
    private List<DrawData> instances = new();
    private int simplePlaneSize = sizeof(float) * 4;

    private SubMeshInstance[] subMeshInstances;
    private ComputeBuffer _drawDataBuffer;
    private ComputeBuffer _simplePlaneBuffer;

    private bool ready = false;

    void Init()
    {
        subMeshInstances = new SubMeshInstance[materials.Length];
        
        _simplePlaneBuffer = new ComputeBuffer(6, simplePlaneSize);
        
        for (int i = 0; i < materials.Length; i++)
        {
            subMeshInstances[i] = new SubMeshInstance(
                mesh.GetIndexCount(i), 
                mesh.GetIndexStart(i), 
                mesh.GetBaseVertex(i), 
                materials[i]
            );
        }
    }
    
    // Render each 1023-sized batch one time
    private void RenderBatches()
    {
        // returns 6 planes
        // [0] = Left, [1] = Right, [2] = Down,
        // [3] = Up,   [4] = Near,  [5] = Far
        SimplePlane[] planes = GetFrustumPlanes(agentCamera);
        
        _simplePlaneBuffer.SetData(planes);
        
        for (int i = 0; i < materials.Length; i++)
        {
            // i is the submeshIndex 
            Graphics.DrawMeshInstancedIndirect(
                mesh, 
                i, 
                subMeshInstances[i].material, 
                new Bounds(Vector3.zero, Vector3.one * 1000f),
                subMeshInstances[i].argsBuffer
            );
        }
    }
    
    void OnDestroy()
    {
        for (int i = 0; i < subMeshInstances.Length; i++)
            subMeshInstances[i].Release();
        _drawDataBuffer?.Release();
        _simplePlaneBuffer?.Release();
    }

    // Update is called once per frame
    void Update()
    {
        // Continuously render batches only when Init() has been called
        if (ready) RenderBatches();
    }

    private SimplePlane[] GetFrustumPlanes(Camera mainCamera)
    {
        Plane[] planes = GeometryUtility.CalculateFrustumPlanes(mainCamera);
        
        SimplePlane[] simplePlanes = new SimplePlane[6];
        
        for (int i = 0; i < 6; i++)
        {
            SimplePlane sPlane = new SimplePlane();
            sPlane.distance = planes[i].distance;
            sPlane.normal = planes[i].normal;
            simplePlanes[i] = sPlane; 
        }

        return simplePlanes;
    }
    
    public void AddObjectToBatch(DrawData m)
    {
        if (subMeshInstances == null) Init();
        
        instances.Add(m);
        
        // TODO: Could be better but it works for now
        _drawDataBuffer?.Release();
        _drawDataBuffer = new ComputeBuffer(instances.Count, Marshal.SizeOf<DrawData>());
        _drawDataBuffer.SetData(instances);
        
        foreach (var submesh in subMeshInstances)
        {
            submesh.UpdateArgs(instances.Count);
            submesh.material.SetBuffer("_DrawData", _drawDataBuffer);
        }
        
        ready = true;
    }
}
