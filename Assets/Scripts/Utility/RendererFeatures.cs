using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering.Universal;

// Runtime access to the active URP renderer's features, including the SSAO feature's internal settings.
// Changes are in-memory only; they are not saved to the renderer asset.
public static class RendererFeatures
{
    public const string Ssao = "ScreenSpaceAmbientOcclusion";
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static ScriptableRendererFeature Find(string featureName)
    {
        ScriptableRenderer renderer = UniversalRenderPipeline.asset != null ? UniversalRenderPipeline.asset.GetRenderer(0) : null;
        var features = typeof(ScriptableRenderer).GetProperty("rendererFeatures", Flags)?.GetValue(renderer)
            as IEnumerable<ScriptableRendererFeature>;
        ScriptableRendererFeature feature = features?.FirstOrDefault(f => f != null && f.name == featureName);
        if (feature == null) Debug.LogWarning($"Renderer feature '{featureName}' was not found.");
        return feature;
    }

    // Reads an SSAO settings field (e.g. "Downsample", "Samples"); enums come back as their enum type.
    public static object GetSsaoSetting(string fieldName)
    {
        object settings = SsaoSettingsObject();
        return SsaoField(settings, fieldName)?.GetValue(settings);
    }

    // Sets an SSAO settings field; enums take an int. Applies from the next frame.
    public static bool SetSsaoSetting(string fieldName, object value)
    {
        object settings = SsaoSettingsObject();
        FieldInfo field = SsaoField(settings, fieldName);
        if (field == null) return false;
        field.SetValue(settings, field.FieldType.IsEnum ? Enum.ToObject(field.FieldType, value) : value);
        return true;
    }

    private static object SsaoSettingsObject()
    {
        ScriptableRendererFeature feature = Find(Ssao);
        return feature?.GetType().GetField("m_Settings", Flags)?.GetValue(feature);
    }

    private static FieldInfo SsaoField(object settings, string fieldName)
    {
        FieldInfo field = settings?.GetType().GetField(fieldName, Flags);
        if (field == null) Debug.LogWarning($"SSAO setting '{fieldName}' could not be resolved.");
        return field;
    }
}
