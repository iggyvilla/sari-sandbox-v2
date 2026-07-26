using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

public static class PriceTagBaker
{
    private const string PriceDataPath = "Assets/Resources/Data/PriceData.json";
    private const string PriceTagPrefabPath = "Assets/Prefabs/Paper Price Tag (Plane).prefab";
    private const string OutputDirectory = "Assets/Resources/Generated/PriceTags";
    private const string DebugOutputDirectory = "Assets/Editor/Generated/PriceTags";
    private const string AtlasPath = "Assets/Resources/Generated/PriceTags.spriteatlas";

    [MenuItem("Tools/Price Tags/Bake All Price Tags")]
    public static void BakeAllPriceTags()
    {
        if (!TryLoadDependencies("bake price tags", out Dictionary<string, ItemPriceData> priceData, out GameObject prefab))
        {
            return;
        }

        if (!ValidatePriceTagPrefab(prefab)) return;

        int bakedCount = PrefabSpriteBaker.BakeSprites(
            CreateSettings(prefab),
            priceData,
            item => item.Key,
            (instance, item) =>
            {
                instance.GetComponent<PriceTag>().SetValues(
                    item.Key,
                    item.Value.pricePHP,
                    item.Value.netWeight
                );
            },
            false
        );

        Debug.Log($"Baked {bakedCount} price tags to {OutputDirectory}.");
    }

    [MenuItem("Tools/Price Tags/Open Bake Debug Scene")]
    public static void OpenBakeDebugScene()
    {
        if (!TryLoadDependencies(
                "open price tag bake debug scene",
                out Dictionary<string, ItemPriceData> priceData,
                out GameObject prefab
            ))
        {
            return;
        }

        if (!ValidatePriceTagPrefab(prefab)) return;

        Scene debugScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        debugScene.name = "Price Tag Bake Debug";

        PrefabSpriteBaker.BakeSetup setup =
            PrefabSpriteBaker.CreateBakeSetup(debugScene, CreateSettings(prefab), true, false);

        KeyValuePair<string, ItemPriceData> sample = GetFirstItem(priceData);
        setup.SourceInstance.GetComponent<PriceTag>().SetValues(
            sample.Key,
            sample.Value.pricePHP,
            sample.Value.netWeight
        );
        PrefabSpriteBaker.ForceTextUpdate(setup.SourceInstance);

        Bounds bounds = setup.BackingRenderer.bounds;
        PrefabSpriteBaker.PositionCameraAndLight(setup, bounds);

        Selection.objects = new Object[] { setup.SourceInstance, setup.CameraObject, setup.LightObject };
        SceneView.lastActiveSceneView?.FrameSelected();

        Debug.Log(
            $"Opened visible price tag bake debug scene using sample '{sample.Key}'. " +
            "The selected camera/light/tag match the baker setup."
        );
    }

    [MenuItem("Tools/Price Tags/Render Debug Sample PNG")]
    public static void RenderDebugSamplePng()
    {
        if (!TryLoadDependencies(
                "render price tag debug sample",
                out Dictionary<string, ItemPriceData> priceData,
                out GameObject prefab
            ))
        {
            return;
        }

        if (!ValidatePriceTagPrefab(prefab)) return;

        System.IO.Directory.CreateDirectory(DebugOutputDirectory);

        Scene previewScene = EditorSceneManager.NewPreviewScene();
        PrefabSpriteBaker.BakeSetup setup = default;
        RenderTexture renderTexture = null;

        try
        {
            setup = PrefabSpriteBaker.CreateBakeSetup(previewScene, CreateSettings(prefab), true, true);

            KeyValuePair<string, ItemPriceData> sample = GetFirstItem(priceData);
            setup.SourceInstance.GetComponent<PriceTag>().SetValues(
                sample.Key,
                sample.Value.pricePHP,
                sample.Value.netWeight
            );
            PrefabSpriteBaker.ForceTextUpdate(setup.SourceInstance);

            Bounds bounds = setup.BackingRenderer.bounds;
            PrefabSpriteBaker.PositionCameraAndLight(setup, bounds);

            string outputPath = $"{DebugOutputDirectory}/__DEBUG_SAMPLE.png";
            PrefabSpriteBaker.RenderPng(setup.Camera, ref renderTexture, bounds, outputPath);
            PrefabSpriteBaker.ConfigureSpriteImport(outputPath);
            AssetDatabase.Refresh();

            Debug.Log($"Rendered debug sample with the exact bake path: {outputPath}");
        }
        finally
        {
            PrefabSpriteBaker.ReleaseRenderTexture(ref renderTexture);
            PrefabSpriteBaker.DestroyBakeSetup(setup);
            EditorSceneManager.ClosePreviewScene(previewScene);
        }
    }

    private static PrefabSpriteBaker.BakeSettings CreateSettings(GameObject prefab)
    {
        return new PrefabSpriteBaker.BakeSettings
        {
            Prefab = prefab,
            PrefabPath = PriceTagPrefabPath,
            OutputDirectory = OutputDirectory,
            AtlasPath = AtlasPath,
            SourceObjectName = "Price Tag",
            SourceRotation = Quaternion.Euler(90f, 180f, 0f)
        };
    }

    private static bool TryLoadDependencies(
        string operation,
        out Dictionary<string, ItemPriceData> priceData,
        out GameObject prefab
    )
    {
        priceData = null;
        prefab = null;

        TextAsset priceDataAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(PriceDataPath);
        if (priceDataAsset == null)
        {
            Debug.LogError($"Cannot {operation}. Missing price data at {PriceDataPath}.");
            return false;
        }

        prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PriceTagPrefabPath);
        if (prefab == null)
        {
            Debug.LogError($"Cannot {operation}. Missing price tag prefab at {PriceTagPrefabPath}.");
            return false;
        }

        priceData = JsonConvert.DeserializeObject<Dictionary<string, ItemPriceData>>(priceDataAsset.text);
        if (priceData == null || priceData.Count == 0)
        {
            Debug.LogError($"Cannot {operation}. Price data is empty or invalid.");
            return false;
        }

        return true;
    }

    private static bool ValidatePriceTagPrefab(GameObject prefab)
    {
        if (prefab.GetComponent<PriceTag>() == null)
        {
            Debug.LogError($"{PriceTagPrefabPath} must contain a {nameof(PriceTag)} component.");
            return false;
        }

        if (prefab.GetComponent<MeshRenderer>() == null)
        {
            Debug.LogError($"{PriceTagPrefabPath} must contain a root {nameof(MeshRenderer)} for bake framing.");
            return false;
        }

        return true;
    }

    private static KeyValuePair<string, ItemPriceData> GetFirstItem(
        Dictionary<string, ItemPriceData> priceData
    )
    {
        foreach (KeyValuePair<string, ItemPriceData> item in priceData)
        {
            return item;
        }

        return default;
    }
}
