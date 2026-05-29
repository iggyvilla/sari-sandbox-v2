using System.Collections.Generic;
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

    // LOD2/LOD3 are disabled until their mesh scales are fixed. Flip to true (here or in
    // the Inspector) to restore all 4 LODs — no other code change needed.
    [SerializeField] private bool enableLod2AndLod3 = false;

    // Default max-distance thresholds (ascending: inner → outer).
    // lods[i].maxDistance = "use this LOD when closer than this distance".
    // lods[0] = LOD0 (closest/highest quality); last entry = hard cull distance (skip entirely beyond this).
    private static readonly float[] DefaultMaxDistances = { 5f, 7f, 10f, 15f };

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
        // Resolve _LOD0–_LOD3 child transforms via the shared resolver (same scan + carry-forward
        // fallback used by ItemSpawner). Missing LODs reuse the last found one, so single-LOD
        // products stay visible through the normal far-distance buckets.
        Transform[] lodTransforms = LodHierarchy.ResolveLodTransforms(obj);

        // LOD2/LOD3 are disabled by default; cap to 2 active LODs until their scales are fixed.
        int activeLods = enableLod2AndLod3 ? LodHierarchy.MaxLods : 2;
        var lodList = new List<LODDefinition>();

        for (int i = 0; i < activeLods; i++)
        {
            Transform t = lodTransforms[i];
            var mf = t.GetComponent<MeshFilter>();
            var mr = t.GetComponent<MeshRenderer>();
            if (mf == null || mr == null) continue;

            // The last active LOD is the catch-all: give it the original hard-cull distance so
            // disabling LOD2/LOD3 preserves the full visibility range instead of shrinking it.
            float maxDistance = (i == activeLods - 1)
                ? DefaultMaxDistances[LodHierarchy.MaxLods - 1]
                : DefaultMaxDistances[i];

            lodList.Add(new LODDefinition
            {
                mesh        = mf.sharedMesh,
                materials   = CloneMaterialsForInstancing(mr.sharedMaterials),
                maxDistance = maxDistance
            });
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
