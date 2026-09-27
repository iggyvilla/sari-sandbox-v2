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

    private const int TextureHeight = 256;
    private const int CompressionBlockSize = 4;
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
        public int MaxTextureDimension;
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
            List<string> outputPaths = new List<string>(values.Count);
            foreach (T value in values)
            {
                BakeSetup setup = CreateBakeSetup(previewScene, settings, true, true);
                try
                {
                    Bounds bounds = PrepareSetup(setup, instance => configureInstance(instance, value));

                    string assetName = SanitizeAssetFileName(getAssetName(value));
                    string outputPath = $"{settings.OutputDirectory}/{assetName}.png";
                    RenderPng(setup.Camera, ref renderTexture, bounds, outputPath, settings.MaxTextureDimension);
                    outputPaths.Add(outputPath);
                }
                finally
                {
                    DestroyBakeSetup(setup);
                }
            }

            ConfigureSpriteImports(outputPaths);
            AssetDatabase.SaveAssets();
            RebuildAtlas(settings.OutputDirectory, settings.AtlasPath);
            return outputPaths.Count;
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

    /// <summary>
    /// Applies per-sprite values to the source instance, refreshes its text, and frames the camera/light.
    /// Returns the framing bounds used for rendering.
    /// </summary>
    public static Bounds PrepareSetup(BakeSetup setup, Action<GameObject> configureInstance)
    {
        configureInstance(setup.SourceInstance);
        ForceTextUpdate(setup.SourceInstance);

        Bounds bounds = setup.BackingRenderer.bounds;
        PositionCameraAndLight(setup, bounds);
        return bounds;
    }

    private static void ForceTextUpdate(GameObject root)
    {
        Canvas.ForceUpdateCanvases();

        foreach (TMP_Text text in root.GetComponentsInChildren<TMP_Text>(true))
        {
            text.ForceMeshUpdate(true, true);
        }
    }

    private static void PositionCameraAndLight(BakeSetup setup, Bounds bounds)
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
        string outputPath,
        int maxTextureDimension = 0
    )
    {
        Vector2Int textureSize = CalculateTextureSize(bounds, maxTextureDimension);
        if (renderTexture == null || renderTexture.width != textureSize.x || renderTexture.height != textureSize.y)
        {
            ReleaseRenderTexture(ref renderTexture);
            renderTexture = new RenderTexture(textureSize.x, textureSize.y, 24, RenderTextureFormat.ARGB32)
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

            File.WriteAllBytes(outputPath, RenderTextureUtility.EncodeToPng(renderTexture, TextureFormat.RGBA32));
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
        ApplySpriteImportSettings(outputPath);
    }

    // Imports freshly written PNGs once, then applies sprite settings in a single batched reimport.
    private static void ConfigureSpriteImports(IEnumerable<string> outputPaths)
    {
        AssetDatabase.Refresh();
        AssetDatabase.StartAssetEditing();
        try
        {
            foreach (string outputPath in outputPaths)
            {
                ApplySpriteImportSettings(outputPath);
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
        }
    }

    private static void ApplySpriteImportSettings(string outputPath)
    {
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
        importer.npotScale = TextureImporterNPOTScale.None;
        importer.maxTextureSize = 1024;
        importer.textureCompression = TextureImporterCompression.CompressedHQ;

        importer.SetPlatformTextureSettings(
            ApplyStandaloneBc7(importer.GetPlatformTextureSettings("Standalone"), 1024)
        );
        importer.SaveAndReimport();
    }

    private static TextureImporterPlatformSettings ApplyStandaloneBc7(
        TextureImporterPlatformSettings settings,
        int maxTextureSize
    )
    {
        settings.name = "Standalone";
        settings.overridden = true;
        settings.maxTextureSize = maxTextureSize;
        settings.format = TextureImporterFormat.BC7;
        settings.textureCompression = TextureImporterCompression.CompressedHQ;
        settings.compressionQuality = 100;
        settings.crunchedCompression = false;
        return settings;
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

        SpriteAtlasExtensions.SetPlatformSettings(
            atlas,
            ApplyStandaloneBc7(new TextureImporterPlatformSettings(), 2048)
        );

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
        if (settings.MaxTextureDimension < 0)
            throw new ArgumentException("The maximum texture dimension cannot be negative.", nameof(settings));
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

    private static Vector2Int CalculateTextureSize(Bounds bounds, int maxTextureDimension)
    {
        if (bounds.size.y <= Mathf.Epsilon)
        {
            throw new InvalidOperationException(
                $"Cannot bake sprite because its framing renderer height is {bounds.size.y}. " +
                "Check the prefab orientation and bake source rotation."
            );
        }

        int textureWidth = AlignToCompressionBlock(
            (int)Math.Ceiling(TextureHeight * (double)bounds.size.x / bounds.size.y)
        );
        int maxTextureSize = SystemInfo.maxTextureSize;
        int effectiveMaxDimension = maxTextureDimension > 0
            ? Math.Min(maxTextureDimension, maxTextureSize)
            : maxTextureSize;
        int textureHeight = TextureHeight;
        int largestDimension = Math.Max(textureWidth, textureHeight);
        if (largestDimension > effectiveMaxDimension)
        {
            double scale = (double)effectiveMaxDimension / largestDimension;
            textureWidth = AlignToCompressionBlock((int)Math.Floor(textureWidth * scale));
            textureHeight = AlignToCompressionBlock((int)Math.Floor(textureHeight * scale));

            return new Vector2Int(
                Math.Max(CompressionBlockSize, textureWidth),
                Math.Max(CompressionBlockSize, textureHeight)
            );
        }

        return new Vector2Int(Math.Max(CompressionBlockSize, textureWidth), textureHeight);
    }

    private static int AlignToCompressionBlock(int dimension)
    {
        int clamped = Math.Max(CompressionBlockSize, dimension);
        return (clamped + CompressionBlockSize - 1) / CompressionBlockSize * CompressionBlockSize;
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
