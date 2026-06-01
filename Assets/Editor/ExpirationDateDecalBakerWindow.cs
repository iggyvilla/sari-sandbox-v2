using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

public sealed class ExpirationDateDecalBakerWindow : EditorWindow
{
    private const string OutputDirectory = "Assets/Resources/Generated/ExpirationDateDecals";
    private const string AtlasPath = "Assets/Resources/Generated/ExpirationDateDecals.spriteatlas";
    private const string PrefabPathEditorPref = "Sari.ExpirationDateDecalBaker.PrefabPath";
    private const string ReferenceDateFormat = "yyyy-MM-dd";
    private const int MaxTextureDimension = 1024;

    private GameObject decalPrefab;
    private string referenceDateText;
    private int count = 100;
    private int minimumMonths = 1;
    private int maximumMonths = 12;
    private int seed;
    private string message;
    private MessageType messageType;

    [MenuItem("Tools/Expiration Date Decals/Bake...")]
    public static void Open()
    {
        GetWindow<ExpirationDateDecalBakerWindow>("Expiration Date Decals");
    }

    private void OnEnable()
    {
        referenceDateText ??= DateTime.Today.ToString(ReferenceDateFormat, CultureInfo.InvariantCulture);

        string prefabPath = EditorPrefs.GetString(PrefabPathEditorPref, string.Empty);
        if (!string.IsNullOrEmpty(prefabPath))
        {
            decalPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        }
    }

    private void OnDisable()
    {
        RememberPrefabPath();
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Expiration Date Decal Bake Settings", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        EditorGUI.BeginChangeCheck();
        decalPrefab = (GameObject)EditorGUILayout.ObjectField("Decal Prefab", decalPrefab, typeof(GameObject), false);
        if (EditorGUI.EndChangeCheck())
        {
            RememberPrefabPath();
        }

        referenceDateText = EditorGUILayout.TextField("Reference Date", referenceDateText);
        count = EditorGUILayout.IntField("Date Count", count);
        minimumMonths = EditorGUILayout.IntField("Minimum Months", minimumMonths);
        maximumMonths = EditorGUILayout.IntField("Maximum Months", maximumMonths);
        seed = EditorGUILayout.IntField("Random Seed", seed);

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "The selected prefab must have a root MeshRenderer and an ExpirationDateDecalHandler.",
            MessageType.Info
        );

        if (!string.IsNullOrEmpty(message))
        {
            EditorGUILayout.HelpBox(message, messageType);
        }

        if (GUILayout.Button("Bake Expiration Date Decals"))
        {
            Bake();
        }
    }

    private void Bake()
    {
        if (!TryValidatePrefab(out string prefabPath))
        {
            return;
        }

        if (!DateTime.TryParseExact(
                referenceDateText,
                ReferenceDateFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime referenceDate
            ))
        {
            SetError($"Reference date must use the {ReferenceDateFormat} format.");
            return;
        }

        IReadOnlyList<DateTime> dates;
        try
        {
            dates = ExpirationDateGenerator.Generate(
                referenceDate,
                count,
                minimumMonths,
                maximumMonths,
                seed
            );
        }
        catch (ArgumentException exception)
        {
            SetError(exception.Message);
            return;
        }

        PrefabSpriteBaker.BakeSettings settings = new()
        {
            Prefab = decalPrefab,
            PrefabPath = prefabPath,
            OutputDirectory = OutputDirectory,
            AtlasPath = AtlasPath,
            SourceObjectName = "Expiration Date Decal",
            MaxTextureDimension = MaxTextureDimension
        };

        int bakedCount;
        try
        {
            bakedCount = PrefabSpriteBaker.BakeSprites(
                settings,
                dates,
                date => date.ToString(ReferenceDateFormat, CultureInfo.InvariantCulture),
                (instance, date) => instance.GetComponent<ExpirationDateDecalHandler>().SetExpirationDate(date),
                true
            );
        }
        catch (Exception exception)
        {
            SetError($"Expiration date decal bake failed: {exception.Message}");
            Debug.LogException(exception);
            return;
        }

        message = $"Baked {bakedCount} expiration date decals to {OutputDirectory}.";
        messageType = MessageType.Info;
        Debug.Log(message);
    }

    private bool TryValidatePrefab(out string prefabPath)
    {
        prefabPath = string.Empty;

        if (decalPrefab == null)
        {
            SetError("Select an expiration date decal prefab before baking.");
            return false;
        }

        prefabPath = AssetDatabase.GetAssetPath(decalPrefab);
        if (string.IsNullOrEmpty(prefabPath) || PrefabUtility.GetPrefabAssetType(decalPrefab) == PrefabAssetType.NotAPrefab)
        {
            SetError("The selected decal must be a prefab asset.");
            return false;
        }

        if (decalPrefab.GetComponent<MeshRenderer>() == null)
        {
            SetError($"The selected prefab must contain a root {nameof(MeshRenderer)} for bake framing.");
            return false;
        }

        ExpirationDateDecalHandler handler = decalPrefab.GetComponent<ExpirationDateDecalHandler>();
        if (handler == null)
        {
            SetError($"The selected prefab must contain an {nameof(ExpirationDateDecalHandler)} component.");
            return false;
        }

        if (!handler.HasTextReference)
        {
            SetError($"The selected prefab's {nameof(ExpirationDateDecalHandler)} must reference a TMP text object.");
            return false;
        }

        RememberPrefabPath();
        return true;
    }

    private void RememberPrefabPath()
    {
        string prefabPath = decalPrefab == null ? string.Empty : AssetDatabase.GetAssetPath(decalPrefab);
        EditorPrefs.SetString(PrefabPathEditorPref, prefabPath);
    }

    private void SetError(string error)
    {
        message = error;
        messageType = MessageType.Error;
        Debug.LogError(error);
    }
}
