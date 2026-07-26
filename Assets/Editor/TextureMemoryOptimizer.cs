using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class TextureMemoryOptimizer
{
    private const string ProductPrefabDirectory = "Assets/Resources/Prefabs/Products";
    private const string ProductTextureDirectory = "Assets/Resources/Textures/";
    private const int ProductMaxTextureSize = 1024;

    [MenuItem("Tools/Texture Memory/Apply Recommended Settings")]
    public static void ApplyRecommendedSettings()
    {
        int optimizedProductTextures = OptimizeProductTextures();
        PriceTagBaker.BakeAllPriceTags();

        Debug.Log(
            $"Texture memory optimization complete. Updated {optimizedProductTextures} " +
            "product texture importer(s) and rebaked the price-tag sprites."
        );
    }

    [MenuItem("Tools/Texture Memory/Optimize Product Textures")]
    private static void OptimizeProductTexturesMenu()
    {
        OptimizeProductTextures();
    }

    public static int OptimizeProductTextures()
    {
        string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { ProductPrefabDirectory });
        var productTexturePaths = new HashSet<string>();

        foreach (string prefabGuid in prefabGuids)
        {
            string prefabPath = AssetDatabase.GUIDToAssetPath(prefabGuid);
            foreach (string dependencyPath in AssetDatabase.GetDependencies(prefabPath, true))
            {
                if (
                    dependencyPath.StartsWith(ProductTextureDirectory) &&
                    AssetImporter.GetAtPath(dependencyPath) is TextureImporter
                )
                {
                    productTexturePaths.Add(dependencyPath);
                }
            }
        }

        int changedCount = 0;
        try
        {
            int index = 0;
            foreach (string texturePath in productTexturePaths)
            {
                index++;
                EditorUtility.DisplayProgressBar(
                    "Optimizing product textures",
                    Path.GetFileName(texturePath),
                    index / (float)productTexturePaths.Count
                );

                var importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
                if (importer == null)
                    continue;

                bool changed = false;
                if (importer.maxTextureSize > ProductMaxTextureSize)
                {
                    importer.maxTextureSize = ProductMaxTextureSize;
                    changed = true;
                }

                bool shouldStreamMips = importer.mipmapEnabled;
                if (importer.streamingMipmaps != shouldStreamMips)
                {
                    importer.streamingMipmaps = shouldStreamMips;
                    changed = true;
                }

                TextureImporterPlatformSettings standaloneSettings =
                    importer.GetPlatformTextureSettings("Standalone");
                if (
                    standaloneSettings.overridden &&
                    standaloneSettings.maxTextureSize > ProductMaxTextureSize
                )
                {
                    standaloneSettings.maxTextureSize = ProductMaxTextureSize;
                    importer.SetPlatformTextureSettings(standaloneSettings);
                    changed = true;
                }

                if (!changed)
                    continue;

                importer.SaveAndReimport();
                changedCount++;
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        AssetDatabase.SaveAssets();
        Debug.Log(
            $"Optimized {changedCount} of {productTexturePaths.Count} product texture importer(s): " +
            $"maximum size {ProductMaxTextureSize}, mip streaming enabled where mipmaps exist."
        );
        return changedCount;
    }
}
