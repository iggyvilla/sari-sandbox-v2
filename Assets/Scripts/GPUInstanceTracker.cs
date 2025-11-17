using System.Collections.Generic;
using UnityEngine;

public struct SimplePlane
{
    public float distance;
    public Vector3 normal;
}

// Singleton class made to track BatchInstancer per Item ID
public class GPUInstanceTracker : MonoBehaviour
{
    
    public static GPUInstanceTracker Instance {get; private set;}
    [SerializeField] private Camera mainCamera;
    public ComputeShader frustumCullingShader;
    [SerializeField] private Shader proceduralUrpLitShader;
    private Dictionary<string, BatchInstancer> trackers = new();
    public SimplePlane[] cameraFrustumPlanes;
    
    void Awake()
    {
        // Assemble Singleton class
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    void Update()
    {
        cameraFrustumPlanes = GetFrustumPlanes(mainCamera);
    }
    
    private SimplePlane[] GetFrustumPlanes(Camera camera)
    {
        Plane[] planes = GeometryUtility.CalculateFrustumPlanes(camera);
        
        SimplePlane[] simplePlanes = new SimplePlane[6];
        
        for (int i = 0; i < 6; i++)
        {
            SimplePlane sPlane = new SimplePlane
            {
                distance = planes[i].distance,
                normal = planes[i].normal
            };
            simplePlanes[i] = sPlane; 
        }

        return simplePlanes;
    }
    
    public void AddToInstance(string itemId, GameObject obj, DrawData drawData)
    {
        if (trackers.ContainsKey(itemId))
        {
            trackers[itemId].AddObjectToBatch(drawData);
        }
        else
        {
            BatchInstancer batchInstancer = gameObject.AddComponent<BatchInstancer>();
            
            PrepareBatchInstancer(batchInstancer, obj, itemId);
            
            batchInstancer.Init();
            batchInstancer.AddObjectToBatch(drawData);
            
            trackers[itemId] = batchInstancer;
        }
    }

    void PrepareBatchInstancer(BatchInstancer batchInstancer, GameObject obj, string itemId)
    {
        // Gets LOD0
        // TODO: implement LOD1 in the future?
        batchInstancer.instanceMesh = obj.GetComponentInChildren<MeshFilter>().sharedMesh;
        batchInstancer.materials = obj.GetComponentInChildren<MeshRenderer>().sharedMaterials;
        batchInstancer.frustumCullingShader = Instantiate(frustumCullingShader);
        batchInstancer.agentCamera = mainCamera;
        batchInstancer.itemId = itemId;
        
        /* Make the material a new clone of itself, since the
         * custom shader renders materials of the same type at
         * the same time, if the same material is used on different
         * meshes, it freaks out */
        for (int i = 0; i < batchInstancer.materials.Length; i++)
        {
            Material currentMaterial = batchInstancer.materials[i];
            currentMaterial = new Material(currentMaterial)
            {
                shader = proceduralUrpLitShader,
                enableInstancing = true
            };
            batchInstancer.materials[i] = currentMaterial;
        }
    }
    
}
