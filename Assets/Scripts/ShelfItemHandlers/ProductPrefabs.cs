using System.Collections.Generic;
using UnityEngine;

// Cached loader for product prefabs under Resources/Prefabs/Products.
public static class ProductPrefabs
{
    private const string ResourcePath = "Prefabs/Products/";
    private static readonly Dictionary<string, GameObject> Cache = new();

    public static GameObject Load(string itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return null;
        if (Cache.TryGetValue(itemId, out GameObject prefab) && prefab != null) return prefab;

        prefab = Resources.Load<GameObject>(ResourcePath + itemId);
        if (prefab != null) Cache[itemId] = prefab;
        return prefab;
    }
}
