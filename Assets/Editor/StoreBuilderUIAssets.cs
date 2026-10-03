using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

/// <summary>Import settings for the generated Store Builder sprites (Tools/generate_store_builder_sprites.py).</summary>
class StoreBuilderSpriteImporter : AssetPostprocessor
{
    const string Folder = "Assets/UI/StoreBuilder/Sprites/";

    void OnPreprocessTexture()
    {
        if (!assetPath.StartsWith(Folder)) return;

        var importer = (TextureImporter)assetImporter;
        string name = Path.GetFileNameWithoutExtension(assetPath);
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.mipmapEnabled = false;
        importer.alphaIsTransparency = true;
        importer.filterMode = FilterMode.Bilinear;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        // Shapes are drawn at 2x, so 200 px per unit renders them at the design size.
        importer.spritePixelsPerUnit = name.StartsWith("icon_") ? 64f : 200f;
        importer.spriteBorder = Border(name);

        var settings = new TextureImporterSettings();
        importer.ReadTextureSettings(settings);
        settings.spriteMeshType = SpriteMeshType.FullRect;
        importer.SetTextureSettings(settings);
    }

    static Vector4 Border(string name) => name switch
    {
        "fill_r8" or "ring_r8" => Vector4.one * 18f,
        "fill_r12" or "ring_r12" => Vector4.one * 28f,
        "fill_pill" or "ring_pill" => Vector4.one * 22f,
        "fill_pill_sm" => Vector4.one * 6f,
        _ => Vector4.zero
    };
}

/// <summary>Builds the TextMeshPro fonts and the theme asset the generated UI uses.</summary>
public static class StoreBuilderUIAssets
{
    public const string UiDir = "Assets/UI/StoreBuilder";
    public const string ThemePath = UiDir + "/SB_Theme.asset";
    const string FontDir = "Assets/Fonts/IBMPlex";

    // ASCII plus the symbols the UI strings use.
    const string ExtraCharacters = "°·×²“”‘’–—…←→↑↓•";

    [MenuItem("Sari/Store Builder UI/1. Generate Fonts")]
    public static void GenerateFonts()
    {
        foreach (string file in Directory.GetFiles(FontDir, "*.ttf"))
            CreateFontAsset(file.Replace('\\', '/'));
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    static void CreateFontAsset(string ttfPath)
    {
        string assetPath = ttfPath.Replace(".ttf", " SDF.asset");
        AssetDatabase.DeleteAsset(assetPath);

        var font = AssetDatabase.LoadAssetAtPath<Font>(ttfPath);
        TMP_FontAsset asset = TMP_FontAsset.CreateFontAsset(
            font, 60, 6, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, false);
        asset.name = Path.GetFileNameWithoutExtension(assetPath);
        AssetDatabase.CreateAsset(asset, assetPath);

        asset.material.name = asset.name + " Material";
        AssetDatabase.AddObjectToAsset(asset.material, asset);
        asset.atlasTexture.name = asset.name + " Atlas";
        AssetDatabase.AddObjectToAsset(asset.atlasTexture, asset);

        var characters = new System.Text.StringBuilder(ExtraCharacters);
        for (char c = ' '; c <= '~'; c++) characters.Append(c);
        asset.TryAddCharacters(characters.ToString(), out string missing);
        if (!string.IsNullOrEmpty(missing)) Debug.LogWarning($"{asset.name} has no glyphs for: {missing}");

        // Static: the atlas is fixed, so it does not grow (and churn in git) during play.
        asset.atlasPopulationMode = AtlasPopulationMode.Static;
        EditorUtility.SetDirty(asset);
        Debug.Log($"Created {assetPath} ({asset.characterTable.Count} glyphs)");
    }

    [MenuItem("Sari/Store Builder UI/2. Create Theme")]
    public static SB_Theme CreateTheme()
    {
        var theme = AssetDatabase.LoadAssetAtPath<SB_Theme>(ThemePath);
        if (theme == null)
        {
            theme = ScriptableObject.CreateInstance<SB_Theme>();
            AssetDatabase.CreateAsset(theme, ThemePath);
        }

        theme.sans = LoadFont("IBMPlexSans-Regular");
        theme.sansMedium = LoadFont("IBMPlexSans-Medium");
        theme.sansSemiBold = LoadFont("IBMPlexSans-SemiBold");
        theme.mono = LoadFont("IBMPlexMono-Regular");
        theme.monoMedium = LoadFont("IBMPlexMono-Medium");

        theme.fillR8 = LoadSprite("fill_r8");
        theme.ringR8 = LoadSprite("ring_r8");
        theme.fillR12 = LoadSprite("fill_r12");
        theme.ringR12 = LoadSprite("ring_r12");
        theme.pill = LoadSprite("fill_pill");
        theme.ringPill = LoadSprite("ring_pill");
        theme.pillSmall = LoadSprite("fill_pill_sm");
        theme.circle = LoadSprite("circle");

        var icons = new List<SB_Theme.IconEntry>();
        foreach (string file in Directory.GetFiles(UiDir + "/Sprites", "icon_*.png"))
        {
            string name = Path.GetFileNameWithoutExtension(file).Substring("icon_".Length);
            icons.Add(new SB_Theme.IconEntry { name = name, sprite = LoadSprite("icon_" + name) });
        }
        icons.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        theme.icons = icons.ToArray();

        EditorUtility.SetDirty(theme);
        AssetDatabase.SaveAssets();
        return theme;
    }

    static TMP_FontAsset LoadFont(string name) =>
        AssetDatabase.LoadAssetAtPath<TMP_FontAsset>($"{FontDir}/{name} SDF.asset");

    static Sprite LoadSprite(string name) =>
        AssetDatabase.LoadAssetAtPath<Sprite>($"{UiDir}/Sprites/{name}.png");
}
