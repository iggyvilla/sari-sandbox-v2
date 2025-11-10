using System.Collections.Generic;
using UnityEngine;

// Singleton class made to track BatchInstancer per Item ID
public class GPUInstanceTracker : MonoBehaviour
{
    
    public static GPUInstanceTracker Instance {get; private set;}
    
    private Dictionary<string, BatchInstancer> trackers = new Dictionary<string, BatchInstancer>();
    
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
    
    // option 1: one compute shader per BatchInstancer
    // have to track where in the BatchInstancer batches array an item is
    // also have to track which BatchInstancer an item goes to
    // wasteful if 1 compute shader per BatchInstancer
    // better if 1 compute shader call only every item call
    
    public void AddToInstance(string itemId, GameObject obj, Matrix4x4 matrix)
    {
        if (trackers.ContainsKey(itemId))
        {
            trackers[itemId].AddObjectToBatch(matrix);
        }
        else
        {
            BatchInstancer batchInstancer = gameObject.AddComponent<BatchInstancer>();
            
            batchInstancer.mesh = obj.GetComponentInChildren<MeshFilter>().sharedMesh;
            batchInstancer.materials = obj.GetComponentInChildren<MeshRenderer>().sharedMaterials;

            foreach (var materials in batchInstancer.materials)
            {
                materials.enableInstancing = true;
            }
            
            batchInstancer.AddObjectToBatch(matrix);
            
            trackers[itemId] = batchInstancer;
        }
    }

    
}
