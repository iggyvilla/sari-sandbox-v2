using System;
using UnityEngine;

public enum RenderingPreset { LowMemory, Balanced, HighQuality, Custom }

// Per-machine rendering options: preset table, PlayerPrefs persistence and live apply to GPUInstanceTracker.
// Startup precedence: command-line flags > PlayerPrefs > the tracker's serialized values (Balanced by default).
public static class RenderingSettings
{
    public struct Options : IEquatable<Options>
    {
        public bool textureArrays;
        public bool indirectArgs;
        public TextureArrayResolution resolution;

        public Options(bool textureArrays, bool indirectArgs, TextureArrayResolution resolution)
        {
            this.textureArrays = textureArrays;
            this.indirectArgs = indirectArgs;
            this.resolution = resolution;
        }

        public bool Equals(Options o) =>
            textureArrays == o.textureArrays && indirectArgs == o.indirectArgs && resolution == o.resolution;
    }

    public const string PresetFlag = "-sariRenderPreset";
    private const string ArraysKey = "sari.renderTextureArrays";
    private const string IndirectKey = "sari.renderIndirectArgs";
    private const string ResolutionKey = "sari.renderTextureRes";

    // `keep` is the resolution used when the preset doesn't pick one (Low memory has no arrays).
    public static Options Of(RenderingPreset preset, TextureArrayResolution keep) => preset switch
    {
        RenderingPreset.LowMemory => new Options(false, true, keep),
        RenderingPreset.HighQuality => new Options(true, true, TextureArrayResolution.Res4K),
        _ => new Options(true, true, TextureArrayResolution.Res2K)
    };

    // Custom when the options match no preset (resolution is irrelevant without arrays).
    public static RenderingPreset PresetOf(Options o)
    {
        foreach (RenderingPreset preset in new[] { RenderingPreset.LowMemory, RenderingPreset.Balanced, RenderingPreset.HighQuality })
        {
            Options p = Of(preset, o.resolution);
            if (p.Equals(o)) return preset;
        }
        return RenderingPreset.Custom;
    }

    // Measured extra memory of the packed textures for the full store.
    public static string Cost(TextureArrayResolution resolution) => resolution switch
    {
        TextureArrayResolution.Res1K => "+70 MB",
        TextureArrayResolution.Res2K => "+290 MB",
        _ => "+1 GB"
    };

    public static string Label(RenderingPreset preset) => preset switch
    {
        RenderingPreset.LowMemory => "Low memory (+0 MB)",
        RenderingPreset.Balanced => $"Balanced ({Cost(TextureArrayResolution.Res2K)})",
        RenderingPreset.HighQuality => $"High quality ({Cost(TextureArrayResolution.Res4K)})",
        _ => "Custom"
    };

    public static string Label(TextureArrayResolution resolution) => $"{(int)resolution / 1024}K ({Cost(resolution)})";

    // Saved options if any, then the "-sariRenderPreset low|balanced|hq" flag; `fallback` fills what is unset.
    public static Options Startup(Options fallback)
    {
        var options = new Options(
            PlayerPrefs.GetInt(ArraysKey, fallback.textureArrays ? 1 : 0) != 0,
            PlayerPrefs.GetInt(IndirectKey, fallback.indirectArgs ? 1 : 0) != 0,
            ReadResolution(PlayerPrefs.GetInt(ResolutionKey, (int)fallback.resolution), fallback.resolution));

        string flag = CommandLineArgs.Get(PresetFlag);
        if (string.IsNullOrEmpty(flag)) return options;
        if (TryParsePreset(flag, out RenderingPreset preset)) return Of(preset, options.resolution);
        Debug.LogWarning($"{PresetFlag}: '{flag}' is not low, balanced or hq; ignored.");
        return options;
    }

    // Applies through the tracker's live setters and remembers the choice for next launch.
    public static void Apply(GPUInstanceTracker tracker, Options options)
    {
        if (tracker == null) return;
        // Arrays off first and on last, so the resolution only repacks while arrays are in use.
        if (!options.textureArrays) tracker.SetUseTextureArrays(false);
        tracker.SetTextureArrayResolution(options.resolution);
        tracker.SetUseIndirectArgs(options.indirectArgs);
        if (options.textureArrays) tracker.SetUseTextureArrays(true);
        Save(options);
    }

    public static void Save(Options options)
    {
        PlayerPrefs.SetInt(ArraysKey, options.textureArrays ? 1 : 0);
        PlayerPrefs.SetInt(IndirectKey, options.indirectArgs ? 1 : 0);
        PlayerPrefs.SetInt(ResolutionKey, (int)options.resolution);
        PlayerPrefs.Save();
    }

    private static TextureArrayResolution ReadResolution(int size, TextureArrayResolution fallback) =>
        Enum.IsDefined(typeof(TextureArrayResolution), size) ? (TextureArrayResolution)size : fallback;

    private static bool TryParsePreset(string value, out RenderingPreset preset)
    {
        switch (value.ToLowerInvariant())
        {
            case "low": preset = RenderingPreset.LowMemory; return true;
            case "balanced": preset = RenderingPreset.Balanced; return true;
            case "hq": preset = RenderingPreset.HighQuality; return true;
            default: preset = RenderingPreset.Custom; return false;
        }
    }
}
