using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

// Exports the product catalog and prop footprints that Tools/store_spec validates agent-written stores against.
public static class StoreSpecCatalogExporter
{
    private const string ShelfPrefabPath = "Assets/Prefabs/ShelfBuilder.prefab";
    private const string CheckoutPrefabPath = "Assets/Prefabs/Self Checkout.prefab";
    private const string OutputPath = "Tools/store_spec/catalog.json";

    [MenuItem("Tools/Store Spec/Export Catalog")]
    public static void Export()
    {
        ItemCategories categories = JsonUtility.FromJson<ItemCategories>(
            Resources.Load<TextAsset>("Data/Categories").text);
        GameObject shelf = AssetDatabase.LoadAssetAtPath<GameObject>(ShelfPrefabPath);
        GameObject checkout = AssetDatabase.LoadAssetAtPath<GameObject>(CheckoutPrefabPath);
        if (shelf == null || checkout == null)
        {
            Debug.LogError($"Store spec catalog: missing {ShelfPrefabPath} or {CheckoutPrefabPath}.");
            return;
        }

        // Categories.json is indexed by ItemCategory, so key products by the enum name.
        var products = new Dictionary<string, List<object>>();
        foreach (ItemCategory category in Enum.GetValues(typeof(ItemCategory)))
        {
            var list = new List<object>();
            foreach (string name in categories.Categories[(int)category].Items)
            {
                RetailItemData item = RetailItemData.FromName(name);
                if (item == null) continue;

                RetailItemDimensions d = item.dimensions;
                list.Add(new { name, width = Round(d.width), depth = Round(d.depth), height = Round(d.height) });
            }
            products[category.ToString()] = list;
        }

        Vector3 profile = shelf.GetComponent<ShelfBuilder>().shelfSideProfile.transform.localScale;
        Bounds checkoutBounds = LocalBounds(checkout);

        var catalog = new
        {
            generated_by = "Unity menu: Tools/Store Spec/Export Catalog",
            shelf = new
            {
                depth = Round(profile.z),
                thickness = Round(profile.y),
                inter_item_padding = ItemSpawner.InterItemPadding
            },
            checkout = new
            {
                size = new[] { Round(checkoutBounds.size.x), Round(checkoutBounds.size.z) },
                offset = new[] { Round(checkoutBounds.center.x), Round(checkoutBounds.center.z) }
            },
            products
        };

        File.WriteAllText(OutputPath, JsonConvert.SerializeObject(catalog, Formatting.Indented));
        Debug.Log($"Store spec catalog written to {OutputPath}.");
    }

    // Renderer bounds in the prefab root's local frame (Instantiate replaces the root's rotation).
    private static Bounds LocalBounds(GameObject prefab)
    {
        Transform root = prefab.transform;
        Bounds? local = null;
        foreach (Renderer r in prefab.GetComponentsInChildren<Renderer>())
        {
            Vector3 min = r.bounds.min, max = r.bounds.max;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = new(i % 2 == 0 ? min.x : max.x, i / 2 % 2 == 0 ? min.y : max.y, i / 4 == 0 ? min.z : max.z);
                Vector3 p = root.InverseTransformPoint(corner);
                if (local is Bounds b) { b.Encapsulate(p); local = b; }
                else local = new Bounds(p, Vector3.zero);
            }
        }

        return local ?? new Bounds();
    }

    private static float Round(float value) => (float)Math.Round(value, 4);
}
