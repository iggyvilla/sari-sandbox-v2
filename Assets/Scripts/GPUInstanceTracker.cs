using System.Collections.Generic;
using UnityEngine;

// Singleton class made to track BatchInstancer per Item ID
public class GPUInstanceTracker : MonoBehaviour
{
    
    public static GPUInstanceTracker Instance {get; private set;}
    [SerializeField] private Camera mainCamera;
    public ComputeShader frustumCullingShader;
    [SerializeField] private Shader proceduralUrpLitShader;
    private Dictionary<string, BatchInstancer> trackers = new();
    
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
    
    public void AddToInstance(string itemId, GameObject obj, DrawData drawData)
    {
        if (trackers.ContainsKey(itemId))
        {
            trackers[itemId].AddObjectToBatch(drawData);
        }
        else
        {
            
            BatchInstancer batchInstancer = gameObject.AddComponent<BatchInstancer>();
            
            // Gets LOD0
            // TODO: (maybe implement LOD1 in the future)
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
            
            batchInstancer.Init();
            
            batchInstancer.AddObjectToBatch(drawData);
            trackers[itemId] = batchInstancer;
        }
    }

    
}
