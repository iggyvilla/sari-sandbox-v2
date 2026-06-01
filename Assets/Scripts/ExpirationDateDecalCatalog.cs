using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.U2D;

public static class ExpirationDateDecalCatalog
{
    private const string AtlasResourcePath = "Generated/ExpirationDateDecals";
    private const string CloneSuffix = "(Clone)";
    private static readonly int BaseMapPropertyId = Shader.PropertyToID("Base_Map");

    private sealed class DecalEntry
    {
        public Texture texture;
        public Vector2 uvScale;
        public Vector2 uvBias;
        public float aspectRatio;
    }

    private static readonly Dictionary<string, DecalEntry> Entries = new();
    private static readonly Dictionary<Texture, Material> MaterialsByTexture = new();
    private static readonly List<string> DecalIds = new();
    private static readonly HashSet<string> LoggedWarnings = new();

    private static Material _baseMaterial;
    private static bool _canCreateMaterials;
    private static bool _initialized;

    public static void Initialize(Material baseMaterial)
    {
        DestroyRuntimeMaterials();
        Entries.Clear();
        DecalIds.Clear();
        LoggedWarnings.Clear();
        _baseMaterial = baseMaterial;
        _canCreateMaterials = _baseMaterial != null && _baseMaterial.HasProperty(BaseMapPropertyId);
        _initialized = true;

        if (_baseMaterial == null)
        {
            WarnOnce(
                "missing-base-material",
                $"{nameof(ExpirationDateDecalCatalog)}: no expiration date decal base material is configured."
            );
        }
        else if (!_canCreateMaterials)
        {
            WarnOnce(
                "missing-base-map-property",
                $"{nameof(ExpirationDateDecalCatalog)}: {_baseMaterial.name} has no Base_Map texture property."
            );
        }

        SpriteAtlas atlas = Resources.Load<SpriteAtlas>(AtlasResourcePath);
        if (atlas == null)
        {
            WarnOnce(
                "missing-atlas",
                $"{nameof(ExpirationDateDecalCatalog)}: could not load Resources/{AtlasResourcePath}."
            );
            return;
        }

        Sprite[] sprites = new Sprite[atlas.spriteCount];
        atlas.GetSprites(sprites);
        RegisterSprites(sprites);

        if (Entries.Count == 0)
        {
            RegisterSprites(Resources.LoadAll<Sprite>(AtlasResourcePath));
        }

        DecalIds.AddRange(Entries.Keys);
        if (DecalIds.Count == 0)
        {
            WarnOnce(
                "missing-sprites",
                $"{nameof(ExpirationDateDecalCatalog)}: {AtlasResourcePath} does not contain usable sprites."
            );
        }
    }

    private static void RegisterSprites(IEnumerable<Sprite> sprites)
    {
        foreach (Sprite sprite in sprites)
        {
            if (sprite == null || sprite.texture == null) continue;

            Rect textureRect = sprite.textureRect;
            Texture texture = sprite.texture;

            string decalId = NormalizeSpriteName(sprite.name);
            Entries[decalId] = new DecalEntry
            {
                texture = texture,
                uvScale = new Vector2(textureRect.width / texture.width, textureRect.height / texture.height),
                uvBias = new Vector2(textureRect.x / texture.width, textureRect.y / texture.height),
                aspectRatio = textureRect.width / textureRect.height
            };
        }
    }

    public static string GetRandomDecalId()
    {
        EnsureInitialized();
        return DecalIds.Count == 0 ? null : DecalIds[Random.Range(0, DecalIds.Count)];
    }

    public static void ApplyTo(GameObject item, string decalId)
    {
        if (item == null) return;

        DecalProjector projector = item.GetComponentInChildren<DecalProjector>(true);
        if (projector == null)
        {
            WarnOnce(
                $"missing-projector:{item.name}",
                $"{nameof(ExpirationDateDecalCatalog)}: {item.name} has no child {nameof(DecalProjector)}."
            );
            return;
        }

        EnsureInitialized();
        if (string.IsNullOrEmpty(decalId) || !Entries.TryGetValue(decalId, out DecalEntry entry))
        {
            WarnOnce(
                $"missing-decal:{decalId ?? "<null>"}",
                $"{nameof(ExpirationDateDecalCatalog)}: decal '{decalId ?? "<null>"}' is unavailable. " +
                $"Disabling the {nameof(DecalProjector)} on {item.name}."
            );
            projector.enabled = false;
            return;
        }

        Material material = GetOrCreateMaterial(entry.texture);
        if (material == null)
        {
            WarnOnce(
                "unusable-base-material",
                $"{nameof(ExpirationDateDecalCatalog)}: cannot configure expiration date materials. " +
                $"Disabling the {nameof(DecalProjector)} on {item.name}."
            );
            projector.enabled = false;
            return;
        }

        projector.material = material;
        projector.uvScale = entry.uvScale;
        projector.uvBias = entry.uvBias;
        MatchProjectorAspectRatio(projector, entry.aspectRatio);
        projector.enabled = true;
    }

    private static void EnsureInitialized()
    {
        if (_initialized) return;

        Initialize(DataHandler.Instance != null
            ? DataHandler.Instance.expirationDateDecalMaterial
            : null);
    }

    private static Material GetOrCreateMaterial(Texture texture)
    {
        if (!_canCreateMaterials || texture == null) return null;

        if (MaterialsByTexture.TryGetValue(texture, out Material material))
        {
            return material;
        }

        material = new Material(_baseMaterial)
        {
            name = $"{_baseMaterial.name} ({texture.name})",
            hideFlags = HideFlags.DontSave
        };
        material.SetTexture(BaseMapPropertyId, texture);
        MaterialsByTexture.Add(texture, material);
        return material;
    }

    private static void MatchProjectorAspectRatio(DecalProjector projector, float aspectRatio)
    {
        if (aspectRatio <= 0f) return;

        Vector3 size = projector.size;
        size.y = size.x / aspectRatio;
        projector.size = size;
    }

    private static string NormalizeSpriteName(string spriteName)
    {
        return spriteName.EndsWith(CloneSuffix)
            ? spriteName.Substring(0, spriteName.Length - CloneSuffix.Length)
            : spriteName;
    }

    private static void DestroyRuntimeMaterials()
    {
        foreach (Material material in MaterialsByTexture.Values)
        {
            if (material != null)
            {
                Object.Destroy(material);
            }
        }

        MaterialsByTexture.Clear();
    }

    private static void WarnOnce(string key, string message)
    {
        if (LoggedWarnings.Add(key))
        {
            Debug.LogWarning(message);
        }
    }
}
