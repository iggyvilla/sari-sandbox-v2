using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.U2D;

public static class PriceTagBaker
{
    private const string PriceDataPath = "Assets/Resources/Data/PriceData.json";
    private const string PriceTagPrefabPath = "Assets/Prefabs/Paper Price Tag (Plane).prefab";
    private const string OutputDirectory = "Assets/Resources/Generated/PriceTags";
    private const string AtlasPath = "Assets/Resources/Generated/PriceTags.spriteatlas";
    private const int PreviewLayer = 22;
    private const int DebugLayer = 0;
    private const int TextureHeight = 512;
    private const float PixelsPerUnit = 1024f;
    private const float CameraDistance = 2f;
    private static readonly Vector3 PreviewPosition = new(-250f, -250f, -250f);
    private static readonly Quaternion BakeSourceRotation = Quaternion.Euler(90f, 180f, 0f);

    private struct BakeSetup
    {
        public GameObject CameraObject;
        public Camera Camera;
        public GameObject LightObject;
        public GameObject PriceTagInstance;
        public PriceTag PriceTag;
        public MeshRenderer BackingRenderer;
    }

    [MenuItem("Tools/Price Tags/Bake All Price Tags")]
    public static void BakeAllPriceTags()
    {
        TextAsset priceDataAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(PriceDataPath);
        GameObject priceTagPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PriceTagPrefabPath);

        if (priceDataAsset == null)
        {
            Debug.LogError($"Cannot bake price tags. Missing price data at {PriceDataPath}.");
            return;
        }

        if (priceTagPrefab == null)
        {
            Debug.LogError($"Cannot bake price tags. Missing price tag prefab at {PriceTagPrefabPath}.");
            return;
        }

        Dictionary<string, ItemPriceData> priceData =
            JsonConvert.DeserializeObject<Dictionary<string, ItemPriceData>>(priceDataAsset.text);

        if (priceData == null || priceData.Count == 0)
        {
            Debug.LogError("Cannot bake price tags. Price data is empty or invalid.");
            return;
        }

        Directory.CreateDirectory(OutputDirectory);

        Scene previewScene = EditorSceneManager.NewPreviewScene();
        RenderTexture renderTexture = null;

        try
        {
            int bakedCount = 0;
            foreach (KeyValuePair<string, ItemPriceData> item in priceData)
            {
                BakeSetup setup = CreateBakeSetup(previewScene, priceTagPrefab, true, true, PreviewLayer);
                if (setup.PriceTag == null || setup.BackingRenderer == null)
                {
                    DestroyBakeSetup(setup);
                    return;
                }

                setup.PriceTag.SetValues(item.Key, item.Value.pricePHP, item.Value.netWeight);
                ForceTextUpdate(setup.PriceTagInstance);
                Bounds bounds = setup.BackingRenderer.bounds;
                PositionCamera(setup.Camera, bounds);
                setup.LightObject.transform.rotation = setup.Camera.transform.rotation;

                string outputPath = $"{OutputDirectory}/{SanitizeAssetFileName(item.Key)}.png";
                RenderPng(setup.Camera, ref renderTexture, bounds, outputPath);
                ConfigureSpriteImport(outputPath);
                DestroyBakeSetup(setup);
                bakedCount++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            RebuildAtlas();

            Debug.Log($"Baked {bakedCount} price tags to {OutputDirectory}.");
        }
        finally
        {
            if (renderTexture != null)
            {
                renderTexture.Release();
                Object.DestroyImmediate(renderTexture);
            }
            
            EditorSceneManager.ClosePreviewScene(previewScene);
        }
    }

    [MenuItem("Tools/Price Tags/Open Bake Debug Scene")]
    public static void OpenBakeDebugScene()
    {
        TextAsset priceDataAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(PriceDataPath);
        GameObject priceTagPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PriceTagPrefabPath);

        if (priceDataAsset == null)
        {
            Debug.LogError($"Cannot open price tag bake debug scene. Missing price data at {PriceDataPath}.");
            return;
        }

        if (priceTagPrefab == null)
        {
            Debug.LogError($"Cannot open price tag bake debug scene. Missing price tag prefab at {PriceTagPrefabPath}.");
            return;
        }

        Dictionary<string, ItemPriceData> priceData =
            JsonConvert.DeserializeObject<Dictionary<string, ItemPriceData>>(priceDataAsset.text);

        if (priceData == null || priceData.Count == 0)
        {
            Debug.LogError("Cannot open price tag bake debug scene. Price data is empty or invalid.");
            return;
        }

        Scene debugScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        debugScene.name = "Price Tag Bake Debug";

        BakeSetup setup = CreateBakeSetup(debugScene, priceTagPrefab, true, false, PreviewLayer);
        if (setup.PriceTag == null || setup.BackingRenderer == null) return;

        KeyValuePair<string, ItemPriceData> sample = default;
        foreach (KeyValuePair<string, ItemPriceData> item in priceData)
        {
            sample = item;
            break;
        }

        setup.PriceTag.SetValues(sample.Key, sample.Value.pricePHP, sample.Value.netWeight);
        ForceTextUpdate(setup.PriceTagInstance);

        Bounds bounds = setup.BackingRenderer.bounds;
        PositionCamera(setup.Camera, bounds);
        setup.LightObject.transform.rotation = setup.Camera.transform.rotation;

        Selection.objects = new Object[] { setup.PriceTagInstance, setup.CameraObject, setup.LightObject };
        SceneView.lastActiveSceneView?.FrameSelected();

        Debug.Log(
            $"Opened visible price tag bake debug scene using sample '{sample.Key}'. " +
            "The selected camera/light/tag match the baker setup."
        );
    }

    [MenuItem("Tools/Price Tags/Render Debug Sample PNG")]
    public static void RenderDebugSamplePng()
    {
        TextAsset priceDataAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(PriceDataPath);
        GameObject priceTagPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PriceTagPrefabPath);

        if (priceDataAsset == null || priceTagPrefab == null)
        {
            Debug.LogError("Cannot render price tag debug sample. Missing price data or price tag prefab.");
            return;
        }

        Dictionary<string, ItemPriceData> priceData =
            JsonConvert.DeserializeObject<Dictionary<string, ItemPriceData>>(priceDataAsset.text);

        if (priceData == null || priceData.Count == 0)
        {
            Debug.LogError("Cannot render price tag debug sample. Price data is empty or invalid.");
            return;
        }

        Directory.CreateDirectory(OutputDirectory);

        Scene previewScene = EditorSceneManager.NewPreviewScene();
        BakeSetup setup = default;
        RenderTexture renderTexture = null;

        try
        {
            setup = CreateBakeSetup(previewScene, priceTagPrefab, true, true, PreviewLayer);
            if (setup.PriceTag == null || setup.BackingRenderer == null) return;

            KeyValuePair<string, ItemPriceData> sample = default;
            foreach (KeyValuePair<string, ItemPriceData> item in priceData)
            {
                sample = item;
                break;
            }

            setup.PriceTag.SetValues(sample.Key, sample.Value.pricePHP, sample.Value.netWeight);
            ForceTextUpdate(setup.PriceTagInstance);

            Bounds bounds = setup.BackingRenderer.bounds;
            PositionCamera(setup.Camera, bounds);
            setup.LightObject.transform.rotation = setup.Camera.transform.rotation;

            string outputPath = $"{OutputDirectory}/__DEBUG_SAMPLE.png";
            RenderPng(setup.Camera, ref renderTexture, bounds, outputPath);
            ConfigureSpriteImport(outputPath);
            AssetDatabase.Refresh();

            Debug.Log($"Rendered debug sample with the exact bake path: {outputPath}");
        }
        finally
        {
            if (renderTexture != null)
            {
                renderTexture.Release();
                Object.DestroyImmediate(renderTexture);
            }

            if (setup.CameraObject != null) Object.DestroyImmediate(setup.CameraObject);
            if (setup.LightObject != null) Object.DestroyImmediate(setup.LightObject);
            if (setup.PriceTagInstance != null) Object.DestroyImmediate(setup.PriceTagInstance);
            EditorSceneManager.ClosePreviewScene(previewScene);
        }
    }

    private static BakeSetup CreateBakeSetup(
        Scene scene,
        GameObject priceTagPrefab,
        bool cameraEnabled,
        bool hideObjects,
        int renderLayer
    )
    {
        GameObject cameraObject = new("Price Tag Bake Camera");
        SceneManager.MoveGameObjectToScene(cameraObject, scene);

        Camera camera = cameraObject.AddComponent<Camera>();
        camera.scene = scene;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
        camera.orthographic = true;
        camera.nearClipPlane = 0.01f;
        camera.farClipPlane = 10f;
        camera.cullingMask = 1 << renderLayer;
        camera.allowHDR = false;
        camera.allowMSAA = true;
        camera.enabled = cameraEnabled;
        if (hideObjects) camera.gameObject.hideFlags = HideFlags.HideAndDontSave;

        GameObject lightObject = new("Price Tag Bake Light");
        SceneManager.MoveGameObjectToScene(lightObject, scene);

        Light light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.color = Color.white;
        light.intensity = 1.2f;
        light.cullingMask = 1 << renderLayer;
        if (hideObjects) light.gameObject.hideFlags = HideFlags.HideAndDontSave;

        GameObject priceTagInstance = (GameObject)PrefabUtility.InstantiatePrefab(priceTagPrefab, scene);
        priceTagInstance.name = "Price Tag Bake Source";
        priceTagInstance.transform.SetPositionAndRotation(PreviewPosition, BakeSourceRotation);
        if (hideObjects) priceTagInstance.hideFlags = HideFlags.HideAndDontSave;
        SetLayerRecursively(priceTagInstance.transform, renderLayer);

        PriceTag priceTag = priceTagInstance.GetComponent<PriceTag>();
        if (priceTag == null)
        {
            Debug.LogError($"{PriceTagPrefabPath} must contain a {nameof(PriceTag)} component.");
        }

        MeshRenderer backingRenderer = priceTagInstance.GetComponent<MeshRenderer>();
        if (backingRenderer == null)
        {
            Debug.LogError($"{PriceTagPrefabPath} must contain a root {nameof(MeshRenderer)} for bake framing.");
        }

        return new BakeSetup
        {
            CameraObject = cameraObject,
            Camera = camera,
            LightObject = lightObject,
            PriceTagInstance = priceTagInstance,
            PriceTag = priceTag,
            BackingRenderer = backingRenderer
        };
    }

    private static void DestroyBakeSetup(BakeSetup setup)
    {
        if (setup.CameraObject != null) Object.DestroyImmediate(setup.CameraObject);
        if (setup.LightObject != null) Object.DestroyImmediate(setup.LightObject);
        if (setup.PriceTagInstance != null) Object.DestroyImmediate(setup.PriceTagInstance);
    }

    private static void ForceTextUpdate(GameObject root)
    {
        Canvas.ForceUpdateCanvases();

        foreach (TMP_Text text in root.GetComponentsInChildren<TMP_Text>(true))
        {
            text.ForceMeshUpdate(true, true);
        }
    }

    private static void SetLayerRecursively(Transform root, int layer)
    {
        root.gameObject.layer = layer;

        foreach (Transform child in root)
        {
            SetLayerRecursively(child, layer);
        }
    }

    private static Bounds CalculateRendererBounds(GameObject root)
    {
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        Bounds bounds = renderers[0].bounds;

        for (int i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }

        return bounds;
    }

    private static void PositionCamera(Camera camera, Bounds bounds)
    {
        camera.transform.position = new Vector3(
            bounds.center.x,
            bounds.center.y,
            bounds.min.z - CameraDistance
        );
        camera.transform.rotation = Quaternion.identity;
        camera.orthographicSize = bounds.extents.y;
    }

    private static void RenderPng(Camera camera, ref RenderTexture renderTexture, Bounds bounds, string outputPath)
    {
        int textureWidth = CalculateTextureWidth(bounds);
        if (renderTexture == null || renderTexture.width != textureWidth)
        {
            if (renderTexture != null)
            {
                renderTexture.Release();
                Object.DestroyImmediate(renderTexture);
            }

            renderTexture = new RenderTexture(textureWidth, TextureHeight, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 4
            };
        }

        camera.targetTexture = renderTexture;

        RenderTexture previousRenderTexture = RenderTexture.active;
        RenderTexture.active = renderTexture;

        try
        {
            GL.Clear(true, true, new Color(0f, 0f, 0f, 0f));
            camera.Render();

            Texture2D texture = new(textureWidth, TextureHeight, TextureFormat.RGBA32, false);
            texture.ReadPixels(new Rect(0, 0, textureWidth, TextureHeight), 0, 0);
            texture.Apply();

            File.WriteAllBytes(outputPath, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
        }
        finally
        {
            camera.targetTexture = null;
            RenderTexture.active = previousRenderTexture;
        }
    }

    private static int CalculateTextureWidth(Bounds bounds)
    {
        if (bounds.size.y <= 0f)
        {
            return TextureHeight;
        }

        return Mathf.Max(1, Mathf.CeilToInt(TextureHeight * bounds.size.x / bounds.size.y));
    }

    private static void ConfigureSpriteImport(string outputPath)
    {
        AssetDatabase.ImportAsset(outputPath);

        TextureImporter importer = AssetImporter.GetAtPath(outputPath) as TextureImporter;
        if (importer == null)
        {
            return;
        }

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.spritePixelsPerUnit = PixelsPerUnit;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = false;
        importer.textureCompression = TextureImporterCompression.CompressedHQ;
        importer.SaveAndReimport();
    }

    private static void RebuildAtlas()
    {
        SpriteAtlas atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(AtlasPath);
        if (atlas == null)
        {
            atlas = new SpriteAtlas();
            AssetDatabase.CreateAsset(atlas, AtlasPath);
        }
        else
        {
            Object[] existingPackables = atlas.GetPackables();
            if (existingPackables.Length > 0)
            {
                SpriteAtlasExtensions.Remove(atlas, existingPackables);
            }
        }

        Object generatedFolder = AssetDatabase.LoadAssetAtPath<Object>(OutputDirectory);
        if (generatedFolder != null)
        {
            SpriteAtlasExtensions.Add(atlas, new[] { generatedFolder });
        }

        SpriteAtlasPackingSettings packingSettings = atlas.GetPackingSettings();
        packingSettings.enableRotation = false;
        packingSettings.enableTightPacking = false;
        packingSettings.padding = 4;
        atlas.SetPackingSettings(packingSettings);

        SpriteAtlasTextureSettings textureSettings = atlas.GetTextureSettings();
        textureSettings.readable = false;
        textureSettings.generateMipMaps = false;
        textureSettings.sRGB = true;
        textureSettings.filterMode = FilterMode.Bilinear;
        atlas.SetTextureSettings(textureSettings);

        EditorUtility.SetDirty(atlas);
        AssetDatabase.SaveAssets();
    }

    private static string SanitizeAssetFileName(string fileName)
    {
        foreach (char invalidChar in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalidChar, '_');
        }

        return fileName;
    }
}
