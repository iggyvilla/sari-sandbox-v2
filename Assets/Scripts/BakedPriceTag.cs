using System.Collections.Generic;
using UnityEngine;

public class BakedPriceTag : MonoBehaviour
{
    private const string ResourcePath = "Generated/PriceTags/";
    private static readonly Dictionary<string, Sprite> SpriteCache = new();

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
}
