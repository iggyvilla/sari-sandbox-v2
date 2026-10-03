using System;

// Rendering performance tab of the settings dialog (see RenderingSettings).
public partial class SB_UIHandler
{
    void BindRendering()
    {
        renderPresetSegment.onValueChanged.AddListener(OnRenderPresetChanged);
        textureArraysToggle.onValueChanged.AddListener(on => ApplyRendering(o => { o.textureArrays = on; return o; }));
        indirectDrawToggle.onValueChanged.AddListener(on => ApplyRendering(o => { o.indirectArgs = on; return o; }));
        textureResolutionSegment.onValueChanged.AddListener(OnTextureResolutionChanged);
    }

    void SyncRenderingSettings()
    {
        GPUInstanceTracker tracker = GPUInstanceTracker.Instance;
        if (tracker == null) return;
        RenderingSettings.Options options = tracker.RenderingOptions;
        RenderingPreset preset = RenderingSettings.PresetOf(options);

        // Custom is not a button: no preset is highlighted and the chip shows instead.
        renderPresetSegment.SetValueWithoutNotify((int)preset);
        renderCustomChip.SetActive(preset == RenderingPreset.Custom);
        ShelfEditGroupHandler.SetOn(textureArraysToggle, options.textureArrays);
        textureArraysCostText.text = RenderingSettings.Cost(options.resolution);
        ShelfEditGroupHandler.SetOn(indirectDrawToggle, options.indirectArgs);
        textureResolutionSegment.SetValueWithoutNotify(Array.IndexOf(Enum.GetValues(typeof(TextureArrayResolution)), options.resolution));
        textureResolutionSegment.interactable = options.textureArrays;
        memoryCostText.text = options.textureArrays ? RenderingSettings.Cost(options.resolution) : "+0 MB";
    }

    // Applies `change` to the current options, then refreshes the widgets (the preset may now read Custom).
    // Ignored while the dialog is closed: the editor fires toggle callbacks at start-up with their stale serialized values.
    void ApplyRendering(Func<RenderingSettings.Options, RenderingSettings.Options> change)
    {
        GPUInstanceTracker tracker = GPUInstanceTracker.Instance;
        if (tracker == null || !settingsDialog.gameObject.activeInHierarchy) return;
        RenderingSettings.Apply(tracker, change(tracker.RenderingOptions));
        SyncRenderingSettings();
    }

    void OnRenderPresetChanged(int index)
    {
        var preset = (RenderingPreset)index;
        if (preset == RenderingPreset.Custom) SyncRenderingSettings();
        else ApplyRendering(o => RenderingSettings.Of(preset, o.resolution));
    }

    void OnTextureResolutionChanged(int index) => ApplyRendering(o =>
    {
        o.resolution = (TextureArrayResolution)Enum.GetValues(typeof(TextureArrayResolution)).GetValue(index);
        return o;
    });
}
