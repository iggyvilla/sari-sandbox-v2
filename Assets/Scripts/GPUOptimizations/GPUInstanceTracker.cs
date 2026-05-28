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
    public static GPUInstanceTracker Instance { get; private set; }
    [SerializeField] private Camera mainCamera;
    public ComputeShader frustumCullingShader;
    [SerializeField] private Shader proceduralUrpLitShader;
    private Dictionary<string, BatchInstancer> trackers = new();
    public SimplePlane[] cameraFrustumPlanes;

    // Default upgrade-distance thresholds (descending: outer → inner).
    // lods[i].upgradeDistance = "switch to lods[i+1] when closer than this".
    private static readonly float[] DefaultUpgradeDistances = { 40f, 15f, 5f, 0f };

    void Awake()
    {
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
            return trackers[itemId];
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
            kvp.Value.ClearAllDrawData();
    }

    private SimplePlane[] GetFrustumPlanes(Camera camera)
    {
        Plane[] planes = GeometryUtility.CalculateFrustumPlanes(camera);
        SimplePlane[] simplePlanes = new SimplePlane[6];
        for (int i = 0; i < 6; i++)
        {
            simplePlanes[i] = new SimplePlane
            {
                distance = planes[i].distance,
                normal   = planes[i].normal
            };
        }
        return simplePlanes;
    }

    public void AddToInstance(string itemId, GameObject obj, InstanceData instanceData)
    {
        if (trackers.ContainsKey(itemId))
        {
            trackers[itemId].AddObjectToBatch(instanceData);
        }
        else
        {
            BatchInstancer batchInstancer = gameObject.AddComponent<BatchInstancer>();
            PrepareBatchInstancer(batchInstancer, obj, itemId);
            batchInstancer.Init();
            batchInstancer.AddObjectToBatch(instanceData);
            trackers[itemId] = batchInstancer;
        }
    }

    void PrepareBatchInstancer(BatchInstancer bi, GameObject obj, string itemId)
    {
        // Scan the first child's children for _LOD0–_LOD3 meshes (in order, low→high quality).
        // _LOD0 is always required; higher-index LODs are optional.
        string[] lodSuffixes = { "_LOD0", "_LOD1", "_LOD2", "_LOD3" };
        var lodList = new List<LODDefinition>();
        Transform prodChild = obj.transform.GetChild(0);

        foreach (string suffix in lodSuffixes)
        {
            foreach (Transform child in prodChild)
            {
                if (!child.name.EndsWith(suffix)) continue;
                var mf = child.GetComponent<MeshFilter>();
                var mr = child.GetComponent<MeshRenderer>();
                if (mf == null || mr == null) break;

                lodList.Add(new LODDefinition
                {
                    mesh            = mf.sharedMesh,
                    materials       = CloneMaterialsForInstancing(mr.sharedMaterials),
                    upgradeDistance = DefaultUpgradeDistances[lodList.Count]
                });
                break;
            }
        }

        if (lodList.Count == 0)
        {
            Debug.LogError("No LOD meshes (_LOD0–_LOD3) found on " + obj.name);
            return;
        }

        bi.lods                 = lodList.ToArray();
        bi.frustumCullingShader = Instantiate(frustumCullingShader);
        bi.agentCamera          = mainCamera;
        bi.itemId               = itemId;
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
                shader            = proceduralUrpLitShader,
                enableInstancing  = true
            };
        }
        return cloned;
    }
}
