using System.Collections.Generic;
using JetBrains.Annotations;
using UnityEngine;
using Plane = UnityEngine.Plane;
using Vector3 = UnityEngine.Vector3;

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

    [CanBeNull]
    public BatchInstancer GetBatchInstancerFromId(string itemId)
    {
        if (trackers.ContainsKey(itemId))
        {
            return trackers[itemId];
        }

        return null;
    }

    public Camera MainCamera => mainCamera;

    public void SetCamera(Camera cam)
    {
        mainCamera = cam;
        foreach (BatchInstancer bi in GetComponents<BatchInstancer>())
            bi.agentCamera = cam;
    }

    public void DespawnAllItems()
    {
        Debug.Log("Despawning all items...");

        foreach (KeyValuePair<string, BatchInstancer> kvp in trackers)
        {
            kvp.Value.ClearAllDrawData();
        }
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
    
    public void AddToInstance(string itemId, GameObject obj, InstanceLODData instanceLODData)
    {
        if (trackers.ContainsKey(itemId))
        {
            trackers[itemId].AddObjectToBatch(instanceLODData);
        }
        else
        {
            BatchInstancer batchInstancer = gameObject.AddComponent<BatchInstancer>();

            PrepareBatchInstancer(batchInstancer, obj, itemId);

            batchInstancer.Init();
            batchInstancer.AddObjectToBatch(instanceLODData);

            trackers[itemId] = batchInstancer;
        }
    }

    void PrepareBatchInstancer(BatchInstancer batchInstancer, GameObject obj, string itemId)
    {
        // Traverse the first child's children to find _LOD0 and _LOD1 meshes and renderers
        Transform prodChild = obj.transform.GetChild(0);
        Mesh lod0Mesh = null;
        Mesh lod1Mesh = null;
        MeshRenderer lod0Renderer = null;
        MeshRenderer lod1Renderer = null;

        foreach (Transform child in prodChild)
        {
            MeshFilter mf = child.GetComponent<MeshFilter>();
            if (mf == null) continue;

            if (child.name.EndsWith("_LOD1"))
            {
                lod1Mesh = mf.sharedMesh;
                lod1Renderer = child.GetComponent<MeshRenderer>();
            }
            else if (child.name.EndsWith("_LOD0"))
            {
                lod0Mesh = mf.sharedMesh;
                lod0Renderer = child.GetComponent<MeshRenderer>();
            }
        }

        if (lod0Mesh == null || lod0Renderer == null)
        {
            Debug.LogError("LOD0 mesh/renderer not found on " + obj.name);
            return;
        }

        batchInstancer.lodMesh0 = lod0Mesh;
        batchInstancer.lodMesh1 = lod1Mesh; // may be null — BatchInstancer handles this
        batchInstancer.frustumCullingShader = Instantiate(frustumCullingShader);
        batchInstancer.agentCamera = mainCamera;
        batchInstancer.itemId = itemId;

        batchInstancer.materials0 = CloneMaterialsForInstancing(lod0Renderer.sharedMaterials);

        if (lod1Renderer != null)
            batchInstancer.materials1 = CloneMaterialsForInstancing(lod1Renderer.sharedMaterials);
    }

    /* Make each material a new clone of itself, since the
     * custom shader renders materials of the same type at
     * the same time — if the same material is used on different
     * meshes, it freaks out */
    Material[] CloneMaterialsForInstancing(Material[] source)
    {
        Material[] cloned = new Material[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            cloned[i] = new Material(source[i])
            {
                shader = proceduralUrpLitShader,
                enableInstancing = true
            };
        }
        return cloned;
    }
    
}
