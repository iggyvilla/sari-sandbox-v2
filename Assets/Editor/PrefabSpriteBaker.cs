using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.U2D;
using Object = UnityEngine.Object;

public static class PrefabSpriteBaker
{
    public const int PreviewLayer = 22;

    private const int TextureHeight = 512;
    private const float PixelsPerUnit = 1024f;
    private const float CameraDistance = 2f;
    private static readonly Vector3 PreviewPosition = new(-250f, -250f, -250f);

    public sealed class BakeSettings
    {
        public GameObject Prefab;
        public string PrefabPath;
        public string OutputDirectory;
        public string AtlasPath;
        public string SourceObjectName;
        public Quaternion SourceRotation = Quaternion.identity;
    }

    public struct BakeSetup
    {
        public GameObject CameraObject;
        public Camera Camera;
        public GameObject LightObject;
        public GameObject SourceInstance;
        public MeshRenderer BackingRenderer;
    }

    public static int BakeSprites<T>(
        BakeSettings settings,
        IReadOnlyCollection<T> values,
        Func<T, string> getAssetName,
        Action<GameObject, T> configureInstance,
        bool removeStalePngs
    )
    {
        ValidateSettings(settings);

        if (values == null) throw new ArgumentNullException(nameof(values));
        if (getAssetName == null) throw new ArgumentNullException(nameof(getAssetName));
        if (configureInstance == null) throw new ArgumentNullException(nameof(configureInstance));

        Directory.CreateDirectory(settings.OutputDirectory);
        if (removeStalePngs)
        {
            RemoveGeneratedPngs(settings.OutputDirectory);
        }

        Scene previewScene = EditorSceneManager.NewPreviewScene();
        RenderTexture renderTexture = null;

        try
        {
            int bakedCount = 0;
            foreach (T value in values)
            {
                BakeSetup setup = CreateBakeSetup(previewScene, settings, true, true);
                try
                {
                    configureInstance(setup.SourceInstance, value);
                    ForceTextUpdate(setup.SourceInstance);

                    Bounds bounds = setup.BackingRenderer.bounds;
                    PositionCameraAndLight(setup, bounds);

                    string assetName = SanitizeAssetFileName(getAssetName(value));
                    string outputPath = $"{settings.OutputDirectory}/{assetName}.png";
                    RenderPng(setup.Camera, ref renderTexture, bounds, outputPath);
                    ConfigureSpriteImport(outputPath);
                    bakedCount++;
                }
                finally
                {
                    DestroyBakeSetup(setup);
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            RebuildAtlas(settings.OutputDirectory, settings.AtlasPath);
            return bakedCount;
        }
        finally
        {
            ReleaseRenderTexture(ref renderTexture);
            EditorSceneManager.ClosePreviewScene(previewScene);
        }
    }

    public static BakeSetup CreateBakeSetup(
        Scene scene,
        BakeSettings settings,
        bool cameraEnabled,
        bool hideObjects,
        int renderLayer = PreviewLayer
    )
    {
        ValidateSettings(settings);

        GameObject cameraObject = new($"{settings.SourceObjectName} Bake Camera");
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

        GameObject lightObject = new($"{settings.SourceObjectName} Bake Light");
        SceneManager.MoveGameObjectToScene(lightObject, scene);

        Light light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.color = Color.white;
        light.intensity = 1.2f;
        light.cullingMask = 1 << renderLayer;
        if (hideObjects) light.gameObject.hideFlags = HideFlags.HideAndDontSave;

        GameObject sourceInstance = (GameObject)PrefabUtility.InstantiatePrefab(settings.Prefab, scene);
        sourceInstance.name = $"{settings.SourceObjectName} Bake Source";
        sourceInstance.transform.SetPositionAndRotation(PreviewPosition, settings.SourceRotation);
        if (hideObjects) sourceInstance.hideFlags = HideFlags.HideAndDontSave;
        SetLayerRecursively(sourceInstance.transform, renderLayer);

        MeshRenderer backingRenderer = sourceInstance.GetComponent<MeshRenderer>();
        if (backingRenderer == null)
        {
            DestroyBakeSetup(new BakeSetup
            {
                CameraObject = cameraObject,
                LightObject = lightObject,
                SourceInstance = sourceInstance
            });

            throw new InvalidOperationException(
                $"{settings.PrefabPath} must contain a root {nameof(MeshRenderer)} for bake framing."
            );
        }

        return new BakeSetup
        {
            CameraObject = cameraObject,
            Camera = camera,
            LightObject = lightObject,
            SourceInstance = sourceInstance,
            BackingRenderer = backingRenderer
        };
    }

    public static void DestroyBakeSetup(BakeSetup setup)
    {
        if (setup.CameraObject != null) Object.DestroyImmediate(setup.CameraObject);
        if (setup.LightObject != null) Object.DestroyImmediate(setup.LightObject);
        if (setup.SourceInstance != null) Object.DestroyImmediate(setup.SourceInstance);
    }

    public static void ForceTextUpdate(GameObject root)
    {
        Canvas.ForceUpdateCanvases();

        foreach (TMP_Text text in root.GetComponentsInChildren<TMP_Text>(true))
        {
            text.ForceMeshUpdate(true, true);
        }
    }

    public static void PositionCameraAndLight(BakeSetup setup, Bounds bounds)
    {
        setup.Camera.transform.position = new Vector3(
            bounds.center.x,
            bounds.center.y,
            bounds.min.z - CameraDistance
        );
        setup.Camera.transform.rotation = Quaternion.identity;
        setup.Camera.orthographicSize = bounds.extents.y;
        setup.LightObject.transform.rotation = setup.Camera.transform.rotation;
    }

    public static void RenderPng(
        Camera camera,
        ref RenderTexture renderTexture,
        Bounds bounds,
        string outputPath
    )
    {
        int textureWidth = CalculateTextureWidth(bounds);
        if (renderTexture == null || renderTexture.width != textureWidth)
        {
            ReleaseRenderTexture(ref renderTexture);
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

    public static void ConfigureSpriteImport(string outputPath)
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

    public static void RebuildAtlas(string outputDirectory, string atlasPath)
    {
        SpriteAtlas atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(atlasPath);
        if (atlas == null)
        {
            atlas = new SpriteAtlas();
            AssetDatabase.CreateAsset(atlas, atlasPath);
        }
        else
        {
            Object[] existingPackables = atlas.GetPackables();
            if (existingPackables.Length > 0)
            {
                SpriteAtlasExtensions.Remove(atlas, existingPackables);
            }
        }

        Object generatedFolder = AssetDatabase.LoadAssetAtPath<Object>(outputDirectory);
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

    public static void ReleaseRenderTexture(ref RenderTexture renderTexture)
    {
        if (renderTexture == null) return;

        renderTexture.Release();
        Object.DestroyImmediate(renderTexture);
        renderTexture = null;
    }

    private static void ValidateSettings(BakeSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        if (settings.Prefab == null) throw new ArgumentException("A prefab is required.", nameof(settings));
        if (string.IsNullOrWhiteSpace(settings.OutputDirectory))
            throw new ArgumentException("An output directory is required.", nameof(settings));
        if (string.IsNullOrWhiteSpace(settings.AtlasPath))
            throw new ArgumentException("An atlas path is required.", nameof(settings));
        if (string.IsNullOrWhiteSpace(settings.SourceObjectName))
            throw new ArgumentException("A source object name is required.", nameof(settings));
    }

    private static void RemoveGeneratedPngs(string outputDirectory)
    {
        foreach (string pngPath in Directory.GetFiles(outputDirectory, "*.png"))
        {
            string assetPath = pngPath.Replace('\\', '/');
            if (!AssetDatabase.DeleteAsset(assetPath))
            {
                File.Delete(pngPath);

                string metaPath = $"{pngPath}.meta";
                if (File.Exists(metaPath))
                {
                    File.Delete(metaPath);
                }
            }
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

    private static int CalculateTextureWidth(Bounds bounds)
    {
        if (bounds.size.y <= Mathf.Epsilon)
        {
            throw new InvalidOperationException(
                $"Cannot bake sprite because its framing renderer height is {bounds.size.y}. " +
                "Check the prefab orientation and bake source rotation."
            );
        }

        double textureWidth = Math.Ceiling(TextureHeight * (double)bounds.size.x / bounds.size.y);
        int maxTextureSize = SystemInfo.maxTextureSize;
        if (textureWidth > maxTextureSize)
        {
            throw new InvalidOperationException(
                $"Cannot bake sprite because its calculated width is {textureWidth:0} pixels, " +
                $"which exceeds this device's {maxTextureSize}-pixel limit. " +
                $"Framing bounds were {bounds.size.x:0.######} x {bounds.size.y:0.######}. " +
                "Check the prefab orientation and bake source rotation."
            );
        }

        return Math.Max(1, (int)textureWidth);
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
