using System.Collections.Generic;
using UnityEngine;

public class BakedPriceTag : MonoBehaviour
{
    private const string ResourcePath = "Generated/PriceTags/";
    private static readonly Dictionary<string, Sprite> SpriteCache = new();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetCache()
    {
        SpriteCache.Clear();
    }

    public static bool TryGetSprite(string itemId, out Sprite sprite)
    {
        if (string.IsNullOrEmpty(itemId))
        {
            sprite = null;
            return false;
        }

        if (SpriteCache.TryGetValue(itemId, out sprite))
        {
            return sprite != null;
        }

        sprite = Resources.Load<Sprite>(ResourcePath + itemId);
        SpriteCache[itemId] = sprite;
        return sprite != null;
    }

    public static void ReleaseCachedSprites()
    {
        var released = new HashSet<Sprite>();
        foreach (Sprite sprite in SpriteCache.Values)
        {
            if (sprite != null && released.Add(sprite))
            {
                Resources.UnloadAsset(sprite);
            }
        }

        SpriteCache.Clear();
    }
}
