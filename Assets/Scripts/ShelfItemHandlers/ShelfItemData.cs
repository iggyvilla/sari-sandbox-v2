using System;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;
using Random = UnityEngine.Random;

// Only the product name is saved; the prefab and its dimensions are resolved at load time.
[Serializable]
public class RetailItemData
{
    public string name;

    [NonSerialized, JsonIgnore]
    public GameObject prefab;

    [NonSerialized, JsonIgnore]
    public RetailItemDimensions dimensions;

    // Null when the product no longer exists or has no mesh.
    public static RetailItemData FromName(string name)
    {
        GameObject product = ProductPrefabs.Load(name);
        if (product == null) return null;

        MeshRenderer r = product.GetComponentInChildren<MeshRenderer>();
        if (r == null)
        {
            Debug.LogError(name + " has no mesh renderer");
            return null;
        }

        return new RetailItemData
        {
            name = name,
            prefab = product,
            dimensions = new RetailItemDimensions
            {
                depth = r.bounds.size.x,
                width = r.bounds.size.z,
                height = r.bounds.size.y
            }
        };
    }
}

[Serializable]
public class RetailItemDimensions
{
    public float depth;
    public float width;
    public float height;
}

[Serializable]
public class SaveDataWrapper
{
    public List<RetailItemData> items;
}

public class ShelfItemData : MonoBehaviour
{
    /*
     * Stores all items that will be stored 
     * on a specific shelf from left to right
     */
    public List<RetailItemData> shelfItems = new();
    public float itemsTotalWidth = 0f;
    private ItemCategories itemCategories;
    private bool _initialized;

    // Bails out of a fill when a category keeps yielding unusable products.
    private const int MaxFailedPicks = 100;
    
    public void RandomFillFromCategory(ItemCategory itemCategory, float interItemPadding, float widthBudget)
    {
        if (!TryInitialize()) return;

        shelfItems = new List<RetailItemData>();
        itemsTotalWidth = 0f;

        float lengthwiseOffset = 0.0f;
        bool firstItem = true;
        int failedPicks = 0;

        while (lengthwiseOffset < widthBudget)
        {
            if (failedPicks >= MaxFailedPicks)
            {
                Debug.LogError($"{nameof(ShelfItemData)} on {name}: no usable products in category {itemCategory}.");
                break;
            }

            RetailItemData retailItemData = GetRandomProduct(itemCategory);
            if (retailItemData == null)
            {
                Debug.LogWarning("Unusable product, retrying...");
                failedPicks++;
                continue;
            }

            RetailItemDimensions dimensions = retailItemData.dimensions;
            float halfWidth = dimensions.width / 2;
            
            lengthwiseOffset += halfWidth + (!firstItem ? interItemPadding : 0);
            
            // If the item we're about to spawn won't fit anymore, end loop
            if (lengthwiseOffset + halfWidth + interItemPadding > widthBudget) break;
            
            // If it does fit within the shelf, add to the item list
            shelfItems.Add(retailItemData);

            lengthwiseOffset += halfWidth;
            firstItem = false;
        }

        itemsTotalWidth = TotalWidth(shelfItems, interItemPadding);
    }

    // Width of the items laid side by side, left to right.
    public static float TotalWidth(List<RetailItemData> items, float interItemPadding)
    {
        float total = 0f;
        foreach (RetailItemData item in items) total += item.dimensions.width;
        return total + Mathf.Max(0, items.Count - 1) * interItemPadding;
    }

    public void SaveItemsToJson(ShelfInfo si)
    {
        if (shelfItems.Count == 0) return;

        string idString = SaveKey(si);

        // Copy so later edits to shelfItems don't mutate the stored data.
        SaveDataWrapper wrapper = new SaveDataWrapper { items = new List<RetailItemData>(shelfItems) };

        DataHandler.Instance.SaveShelfItems(idString, wrapper);
        Debug.Log($"Saved shelf {idString}'s items to store file.");
    }

    public bool LoadItemsFromJson(ShelfInfo si, float interItemPadding)
    {
        string idString = SaveKey(si);

        if (DataHandler.Instance.TryGetShelfItems(idString, out SaveDataWrapper wrapper) && wrapper.items != null)
        {
            Debug.Log($"Loading shelf {idString}'s items from store file.");
            shelfItems = Resolve(wrapper.items, idString);
            itemsTotalWidth = TotalWidth(shelfItems, interItemPadding);
            return true;
        }

        Debug.LogWarning($"Shelf {idString}'s items not found in store file — will generate and save.");
        return false;
    }

    // Fresh resolved copies of saved items, so later edits don't mutate the stored data; skips removed products.
    public static List<RetailItemData> Resolve(List<RetailItemData> saved, string idString)
    {
        List<RetailItemData> resolved = new(saved.Count);
        foreach (RetailItemData itemData in saved)
        {
            if (itemData == null) continue;

            RetailItemData item = RetailItemData.FromName(itemData.name);
            if (item == null)
            {
                Debug.LogWarning($"Shelf {idString}: product '{itemData.name}' no longer exists, skipping.");
                continue;
            }

            resolved.Add(item);
        }

        return resolved;
    }

    public static string KeyPrefix(int shelfId) => $"ID{shelfId}_";

    static string SaveKey(ShelfInfo si) => $"{KeyPrefix(si.shelfId)}{si.subShelfId}_{si.subSubShelfId}";

    RetailItemData GetRandomProduct(ItemCategory itemCategory)
    {
        string[] categoryIds = itemCategories.Categories[(int)itemCategory].Items;
        if (categoryIds == null || categoryIds.Length == 0) return null;

        return RetailItemData.FromName(categoryIds[Random.Range(0, categoryIds.Length)]);
    }

    private bool TryInitialize()
    {
        if (_initialized) return true;

        DataHandler dataHandler = DataHandler.Instance;
        if (dataHandler == null)
        {
            Debug.LogError($"{nameof(ShelfItemData)} on {name}: DataHandler.Instance is missing.");
            return false;
        }

        if (dataHandler.itemCategories == null)
        {
            Debug.LogError($"{nameof(ShelfItemData)} on {name}: DataHandler itemCategories is not loaded.");
            return false;
        }

        itemCategories = dataHandler.itemCategories;
        _initialized = true;
        return true;
    }
}
