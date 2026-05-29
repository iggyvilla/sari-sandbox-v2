using UnityEngine;

// Single source of truth for resolving a product's _LOD0–_LOD3 child transforms.
//
// Both ItemSpawner.GenerateProductDrawData (reads transforms) and
// GPUInstanceTracker.PrepareBatchInstancer (reads meshes/materials) used to scan the
// LOD children independently with DIVERGENT fallback logic. This helper unifies that
// scan + fallback so both sites behave identically.
public static class LodHierarchy
{
    public const int MaxLods = 4;

    // Ordered high-quality/closest (_LOD0) -> low-quality/farthest (_LOD3).
    private static readonly string[] Suffixes = { "_LOD0", "_LOD1", "_LOD2", "_LOD3" };

    // Returns an array of length MaxLods.
    //
    // Fallback semantics (unified = CARRY-FORWARD): a missing LOD slot reuses the last
    // found LOD (LOD0 -> LOD1 -> LOD2 -> LOD3). If _LOD0 itself is absent, slot 0 falls
    // back to the first child (prodChild) and a warning is logged.
    //
    // The returned transforms are guaranteed non-null as long as the product has a child.
    public static Transform[] ResolveLodTransforms(GameObject product)
    {
        Transform prodChild = product.transform.GetChild(0);

        // First pass: record direct matches by suffix.
        Transform[] direct = new Transform[MaxLods];
        foreach (Transform child in prodChild)
        {
            for (int i = 0; i < MaxLods; i++)
            {
                if (child.name.EndsWith(Suffixes[i]))
                {
                    direct[i] = child;
                    break;
                }
            }
        }

        if (direct[0] is null)
        {
            Debug.LogWarning("Could not find _LOD0 for object " + product.name);
        }

        // Second pass: carry forward the last found LOD into any missing slot.
        Transform[] result = new Transform[MaxLods];
        Transform fallback = direct[0] != null ? direct[0] : prodChild;
        for (int i = 0; i < MaxLods; i++)
        {
            if (direct[i] != null) fallback = direct[i];
            result[i] = fallback;
        }

        return result;
    }
}
