using System;
using UnityEngine.UI;

// Rendering performance section of the settings menu (see RenderingSettings).
public partial class SB_UIHandler
{
    void PopulateRenderingDropdowns()
    {
        FillEnumDropdown<RenderingPreset>(renderPresetDropdown, RenderingSettings.Label);
        FillEnumDropdown<TextureArrayResolution>(textureResolutionDropdown, RenderingSettings.Label);
    }

    void SyncRenderingSettings()
    {
        GPUInstanceTracker tracker = GPUInstanceTracker.Instance;
        if (tracker == null) return;
        RenderingSettings.Options options = tracker.RenderingOptions;
        ShelfEditGroupHandler.SetValue(renderPresetDropdown, (int)RenderingSettings.PresetOf(options));
        ShelfEditGroupHandler.SetOn(textureArraysToggle, options.textureArrays);
        if (textureArraysToggle != null)
            textureArraysToggle.GetComponentInChildren<Text>().text = RenderingSettings.Cost(options.resolution);
        ShelfEditGroupHandler.SetOn(indirectDrawToggle, options.indirectArgs);
        ShelfEditGroupHandler.SetValue(
            textureResolutionDropdown, Array.IndexOf(Enum.GetValues(typeof(TextureArrayResolution)), options.resolution));
        if (textureResolutionDropdown != null) textureResolutionDropdown.interactable = options.textureArrays;
    }

    // Applies `change` to the current options, then refreshes the widgets (the preset may now read Custom).
    // Hidden widgets are ignored: the editor fires toggle callbacks at start-up with their stale serialized values.
    void ApplyRendering(Func<RenderingSettings.Options, RenderingSettings.Options> change)
    {
        GPUInstanceTracker tracker = GPUInstanceTracker.Instance;
        if (tracker == null || !agentSettingsMenu.activeInHierarchy) return;
        RenderingSettings.Apply(tracker, change(tracker.RenderingOptions));
        SyncRenderingSettings();
    }

    public void OnRenderPresetChanged(int index)
    {
        var preset = (RenderingPreset)index;
        if (preset == RenderingPreset.Custom) SyncRenderingSettings();
        else ApplyRendering(o => RenderingSettings.Of(preset, o.resolution));
    }

    public void OnTextureArraysToggled(bool on) => ApplyRendering(o => { o.textureArrays = on; return o; });

    public void OnIndirectDrawToggled(bool on) => ApplyRendering(o => { o.indirectArgs = on; return o; });

    public void OnTextureResolutionChanged(int index) => ApplyRendering(o =>
    {
        o.resolution = (TextureArrayResolution)Enum.GetValues(typeof(TextureArrayResolution)).GetValue(index);
        return o;
    });
}
