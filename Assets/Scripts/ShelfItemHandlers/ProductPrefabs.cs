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

    // Instantiates a product as a physics/held item; its LODs and shadows match the GPU-instanced form.
    public static GameObject Spawn(string itemId, Vector3 position, Quaternion rotation, Transform parent = null)
    {
        GameObject prefab = Load(itemId);
        if (prefab == null)
        {
            Debug.LogError($"ProductPrefabs: prefab not found for {itemId}");
            return null;
        }

        GameObject item = Object.Instantiate(prefab, position, rotation, parent);
        item.name = itemId;
        item.tag = "RetailItem";
        ProductLodSetup.Apply(item);
        return item;
    }
}
