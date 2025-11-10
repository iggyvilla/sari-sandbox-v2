using System;
using System.Collections.Generic;
using System.Linq;
using Unity.VisualScripting;
using UnityEngine;

struct SimplePlane
{
    public float distance;
    public Vector3 normal;
}

public class BatchInstancer : MonoBehaviour
{

    [SerializeField] private Camera agentCamera;
    public Mesh mesh;
    public Material[] materials;
    public ComputeShader frustumCullingShader;
    
    private List<List<Matrix4x4>> batches = new();
    private List<List<Vector3>> batchPositions = new();
    private int simplePlaneSize;


    private void Awake()
    {
        // byte size of simplePlane struct
        simplePlaneSize = sizeof(float) * 4;
    }

    // Render each 1023-sized batch one time
    private void RenderBatches()
    {
           foreach (var batch in batches)
           {
               for (int k = 0; k < mesh.subMeshCount; k++)
               {
                   Graphics.DrawMeshInstanced(mesh, k, materials[k],
                       batch);
               }
           }
    }

    // Update is called once per frame
    void Update()
    {
        // Continuously render batches
        RenderBatches();
    }

    private SimplePlane[] PlanesToSimplePlane(Plane[] plane)
    {
        
        SimplePlane[] simplePlanes = new SimplePlane[6];
        
        for (int i = 0; i < 6; i++)
        {
            SimplePlane sPlane = new SimplePlane();
            sPlane.distance = plane[i].distance;
            sPlane.normal = plane[i].normal;
            simplePlanes[i] = sPlane; 
        }

        return simplePlanes;
    }
    
    public void AddObjectToBatch(Matrix4x4 m)
    {
        if (batches.Count == 0) batches.Add(new List<Matrix4x4>());
        
        List<Matrix4x4> recentBatch = batches[batches.Count - 1];

        // get Matrix4x4's position 
        Vector3 itemPos = m.GetColumn(3); 
        
        // Can only draw 1023 of one mesh per draw call
        // we'll probably never reach this many of one item, 
        // but you never know...
        if (recentBatch.Count < 1023)
        {
            recentBatch.Add(m);
        }
        else
        {
            List<Matrix4x4> newBatch = new List<Matrix4x4>();
            newBatch.Add(m);
            batches.Add(newBatch);
        }
    }
}
