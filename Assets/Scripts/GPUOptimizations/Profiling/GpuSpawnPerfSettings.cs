using UnityEngine;

public enum GpuSpawnMaterialMode
{
    Original,
    TexturelessWhite,
    FlatLitSameShader
}

public static class GpuSpawnPerfSettings
{
    private static readonly string[] TexturePropertyNames =
    {
        "_BaseMap",
        "_MainTex",
        "_BumpMap",
        "_EmissionMap",
        "_MetallicGlossMap",
        "_OcclusionMap",
        "_SpecGlossMap",
        "_ParallaxMap"
    };

    public static bool OverridesActive { get; private set; }
    public static bool SyntheticStressActive { get; private set; }
    public static bool SuppressBBoxTriggers { get; private set; }
    public static bool SuppressPriceTags { get; private set; }
    public static bool SuppressExpirationDecals { get; private set; }
    public static bool CaptureVisibleCounts { get; private set; }
    public static GpuSpawnMaterialMode MaterialMode { get; private set; } = GpuSpawnMaterialMode.Original;

    public static void ApplyOverrides(
        GpuSpawnMaterialMode materialMode,
        bool suppressBBoxTriggers,
        bool suppressPriceTags,
        bool suppressExpirationDecals,
        bool syntheticStressActive,
        bool captureVisibleCounts)
    {
        OverridesActive = true;
        MaterialMode = materialMode;
        SuppressBBoxTriggers = suppressBBoxTriggers;
        SuppressPriceTags = suppressPriceTags;
        SuppressExpirationDecals = suppressExpirationDecals;
        SyntheticStressActive = syntheticStressActive;
        CaptureVisibleCounts = captureVisibleCounts;
    }

    public static void ResetOverrides()
    {
        OverridesActive = false;
        SyntheticStressActive = false;
        SuppressBBoxTriggers = false;
        SuppressPriceTags = false;
        SuppressExpirationDecals = false;
        CaptureVisibleCounts = false;
        MaterialMode = GpuSpawnMaterialMode.Original;
    }

    public static void ApplyMaterialOverride(Material material)
    {
        if (!OverridesActive || material == null || MaterialMode == GpuSpawnMaterialMode.Original)
            return;

        ReplaceKnownTextures(material, Texture2D.whiteTexture);

        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", Color.white);
        if (material.HasProperty("_Color"))
            material.SetColor("_Color", Color.white);

        if (MaterialMode != GpuSpawnMaterialMode.FlatLitSameShader)
            return;

        if (material.HasProperty("_Metallic"))
            material.SetFloat("_Metallic", 0f);
        if (material.HasProperty("_Smoothness"))
            material.SetFloat("_Smoothness", 0.15f);
        if (material.HasProperty("_Glossiness"))
            material.SetFloat("_Glossiness", 0.15f);
        if (material.HasProperty("_SpecularHighlights"))
            material.SetFloat("_SpecularHighlights", 0f);
        if (material.HasProperty("_EnvironmentReflections"))
            material.SetFloat("_EnvironmentReflections", 0f);

        material.DisableKeyword("_NORMALMAP");
        material.DisableKeyword("_PARALLAXMAP");
        material.DisableKeyword("_METALLICSPECGLOSSMAP");
        material.DisableKeyword("_OCCLUSIONMAP");
        material.DisableKeyword("_EMISSION");
    }

    private static void ReplaceKnownTextures(Material material, Texture texture)
    {
        for (int i = 0; i < TexturePropertyNames.Length; i++)
        {
            string propertyName = TexturePropertyNames[i];
            if (material.HasProperty(propertyName))
                material.SetTexture(propertyName, texture);
        }
    }
}
