using System.Collections.Generic;
using UnityEngine;

// Transparent product parts (liquids, blended labels/logos) must write depth: instanced draws and physics
// prefabs aren't sorted per bottle, so without it a rear bottle's liquid, label or plastic highlight blends
// over the bottle in front. Only near-clear shells (plastic/glass) keep ZWrite off; they draw last.
// URP Lit re-derives _ZWrite from the surface type on import, so this is applied at runtime, not in assets.
// Blended labels are also pulled to LabelQueue (after liquids, before shells) so they never tie with or draw
// after door glass (queue 3100) or the shell of their own bottle.
public static class ProductMaterials
{
    private const float ShellMaxAlpha = 0.3f;
    private const int LabelQueue = 2995;
    private static readonly int ZWrite = Shader.PropertyToID("_ZWrite");
    private static readonly int Surface = Shader.PropertyToID("_Surface");
    private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
    private static readonly int BaseMap = Shader.PropertyToID("_BaseMap");

    private static readonly Dictionary<Material, Material> Variants = new();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ClearCache() => Variants.Clear();

    public static bool NeedsDepthWrite(Material m) =>
        m != null && m.HasProperty(Surface) && m.GetFloat(Surface) > 0.5f && m.GetFloat(ZWrite) < 0.5f &&
        (IsTextured(m) || (m.HasProperty(BaseColor) && m.GetColor(BaseColor).a >= ShellMaxAlpha));

    // In place, for a material copy the caller owns.
    public static void ApplyDepthWrite(Material m)
    {
        if (!NeedsDepthWrite(m)) return;
        m.SetFloat(ZWrite, 1f);
        if (IsTextured(m) && m.renderQueue > LabelQueue) m.renderQueue = LabelQueue;
    }

    private static bool IsTextured(Material m) => m.HasProperty(BaseMap) && m.GetTexture(BaseMap) != null;

    // Shared depth-writing copy of a source material (cached), or the source itself when it needs none.
    public static Material DepthWritingVariant(Material source)
    {
        if (!NeedsDepthWrite(source)) return source;
        if (Variants.TryGetValue(source, out Material variant) && variant != null) return variant;

        variant = new Material(source) { name = source.name + " (depth write)" };
        ApplyDepthWrite(variant);
        Variants[source] = variant;
        return variant;
    }
}
