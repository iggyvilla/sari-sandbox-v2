using System.Collections.Generic;
using UnityEngine;

// Makes a spawned product prefab look like its GPU-instanced twin: same LODs switching at the same
// distances, same shadow mode. Prefab LODGroups are authored per model (screen-height thresholds that
// vary wildly and often keep empty slots), so they are rebuilt from GPUInstanceTracker's distances.
public static class ProductLodSetup
{
    private const float FallbackFov = 60f;

    public static void Apply(GameObject item)
    {
        if (item.transform.childCount == 0) return;

        List<Renderer> levels = CollectActiveLodRenderers(item, out List<Renderer> unused);
        foreach (Renderer r in unused) r.enabled = false;

        if (item.GetComponentInChildren<LODGroup>() is { } group && levels.Count > 0)
            group.SetLODs(BuildLods(group, levels));

        foreach (Renderer r in item.GetComponentsInChildren<Renderer>(true))
        {
            r.shadowCastingMode = BatchInstancer.ProductShadowMode;
            UseDepthWritingMaterials(r);
        }
    }

    // Same depth-write rule as the GPU path (see ProductMaterials), via shared cached copies.
    private static void UseDepthWritingMaterials(Renderer r)
    {
        Material[] materials = r.sharedMaterials;
        bool changed = false;
        for (int i = 0; i < materials.Length; i++)
        {
            Material variant = ProductMaterials.DepthWritingVariant(materials[i]);
            if (variant == materials[i]) continue;
            materials[i] = variant;
            changed = true;
        }
        if (changed) r.sharedMaterials = materials;
    }

    // One renderer per distinct active LOD; `unused` = LOD children the GPU path never draws.
    private static List<Renderer> CollectActiveLodRenderers(GameObject item, out List<Renderer> unused)
    {
        Transform[] lods = LodHierarchy.ResolveLodTransforms(item);
        int active = GPUInstanceTracker.ActiveLodCount;
        var levels = new List<Renderer>();
        unused = new List<Renderer>();

        for (int i = 0; i < LodHierarchy.MaxLods; i++)
        {
            Renderer r = lods[i].GetComponent<Renderer>();
            if (r == null) continue;
            if (i < active)
            {
                if (!levels.Contains(r)) levels.Add(r);
            }
            else if (!levels.Contains(r) && !unused.Contains(r))
            {
                unused.Add(r);
            }
        }

        unused.RemoveAll(levels.Contains);
        return levels;
    }

    // LOD k -> k+1 switches at the same world distance the GPU path uses; the last LOD never culls.
    private static LOD[] BuildLods(LODGroup group, List<Renderer> levels)
    {
        var lods = new LOD[levels.Count];
        for (int i = 0; i < lods.Length; i++)
        {
            float height = i < lods.Length - 1 ? HeightAtDistance(group, GPUInstanceTracker.LodMaxDistance(i)) : 0f;
            lods[i] = new LOD(height, new[] { levels[i] });
        }
        return lods;
    }

    // Inverse of Unity's LOD selection: screen height * lodBias >= threshold while nearer than `distance`.
    private static float HeightAtDistance(LODGroup group, float distance)
    {
        Vector3 s = group.transform.lossyScale;
        float size = group.size * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
        Camera cam = GPUInstanceTracker.Instance != null ? GPUInstanceTracker.Instance.MainCamera : null;
        float fov = cam != null ? cam.fieldOfView : FallbackFov;
        return size * QualitySettings.lodBias / (2f * distance * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad));
    }
}
