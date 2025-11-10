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
        // returns 6 planes
        // [0] = Left, [1] = Right, [2] = Down,
        // [3] = Up,   [4] = Near,  [5] = Far
        SimplePlane[] planes = PlanesToSimplePlane(
            GeometryUtility.CalculateFrustumPlanes(agentCamera)
        );

        ComputeBuffer planeBuffer = new ComputeBuffer(planes.Length, simplePlaneSize);
        planeBuffer.SetData(planes);
        frustumCullingShader.SetBuffer(0, "planes", planeBuffer);
    
        for (int i = 0; i < batches.Count; i++)
        {
            List<Matrix4x4> batch = new List<Matrix4x4>(batches[i]);
            List<Vector3> batchp = batchPositions[i];
            
            //  input for compute shader
            ComputeBuffer locBuf = new ComputeBuffer(batchp.Count, sizeof(float) * 3);
            locBuf.SetData(batchp);
            // output for compute shader 
            ComputeBuffer cntBuf = new ComputeBuffer(1, sizeof(uint));
            ComputeBuffer idxCullBuff =  new ComputeBuffer(1023, sizeof(uint));
            
            frustumCullingShader.SetBuffer(0, "item_locations", locBuf);
            frustumCullingShader.SetBuffer(0, "out_of_planes", cntBuf);
            frustumCullingShader.SetBuffer(0, "item_mask", idxCullBuff);
            
            frustumCullingShader.Dispatch(0, 1024/64, 1, 1);

            uint[] pointsCulled = new uint[1];
            cntBuf.GetData(pointsCulled);
            
            uint[] indicesToCull = new uint[pointsCulled[0]];
            idxCullBuff.GetData(indicesToCull);

            uint prevIdx = 0;
            foreach (uint idx in indicesToCull)
            {
                if (prevIdx == idx) continue;
                
                prevIdx = idx;
            }
            
            // If all items are going to be culled, don't bother rendering
            if (pointsCulled[0] == 1023)
            {
                for (int j = 0; j < 1023; j++)
                {
                    for (int k = 0; k < mesh.subMeshCount; k++)
                    {
                        Graphics.DrawMeshInstanced(mesh, k, materials[k],
                            batch);
                    }
                }
            }
            
            locBuf.Dispose();
            cntBuf.Dispose();
            idxCullBuff.Dispose();
        }    
        
        planeBuffer.Dispose();
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
        List<Vector3> recentBatchPos = batchPositions[batchPositions.Count - 1];
        // get Matrix4x4's position 
        Vector3 itemPos = m.GetColumn(3); 
        
        // Can only draw 1023 of one mesh per draw call
        // we'll probably never reach this many of one item, 
        // but you never know...
        if (recentBatch.Count < 1023)
        {
            recentBatch.Add(m);
            recentBatchPos.Add(itemPos);
        }
        else
        {
            List<Matrix4x4> newBatch = new List<Matrix4x4>();
            newBatch.Add(m);
            batches.Add(newBatch);
            
            List<Vector3> newBatchPos = new List<Vector3>();
            newBatchPos.Add(itemPos);
            batchPositions.Add(newBatchPos);
        }
    }
}
