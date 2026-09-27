using System;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;
using Random = UnityEngine.Random;

[Serializable]
public class RetailItemData
{
    [NonSerialized, JsonIgnore]
    public GameObject prefab;

    public string name;
    public ItemCategory itemCategory;
    public RetailItemDimensions dimensions;
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
    public float itemsTotalWidth;
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
                itemsTotalWidth = lengthwiseOffset;
                break;
            }

            GameObject product = GetRandomProduct(itemCategory);
            if (product == null)
            {
                Debug.LogWarning("Null product, retrying...");
                failedPicks++;
                continue;
            }
            
            MeshRenderer r = product.GetComponentInChildren<MeshRenderer>();

            if (r == null)
            {
                Debug.LogError(product.name + " has no mesh renderer");
                failedPicks++;
                continue;
            }

            RetailItemDimensions dimensions = new()
            {
                depth = r.bounds.size.x,
                width = r.bounds.size.z,
                height = r.bounds.size.y
            };
            
            RetailItemData retailItemData = new()
            {
                prefab = product,
                name = product.name,
                itemCategory = itemCategory,
                dimensions = dimensions
            };

            float halfWidth = dimensions.width / 2;
            
            lengthwiseOffset += halfWidth + (!firstItem ? interItemPadding : 0);
            
            // If the item we're about to spawn won't fit anymore, end loop
            if (lengthwiseOffset + halfWidth + interItemPadding > widthBudget)
            {
                itemsTotalWidth = lengthwiseOffset - halfWidth;
                break;
            }
            
            // If it does fit within the shelf, add to the item list
            shelfItems.Add(retailItemData);

            lengthwiseOffset += halfWidth;
            firstItem = false;
        }
    }

    public void SaveItemsToJson(ShelfInfo si)
    {
        if (shelfItems.Count == 0) return;

        string idString = SaveKey(si);

        SaveDataWrapper wrapper = new SaveDataWrapper
        {
            // Copy so later edits to shelfItems don't mutate the stored data.
            items = new List<RetailItemData>(shelfItems),
            itemsTotalWidth = itemsTotalWidth
        };

        DataHandler.Instance.SaveShelfItems(idString, wrapper);
        Debug.Log($"Saved shelf {idString}'s items to store file.");
    }

    public bool LoadItemsFromJson(ShelfInfo si)
    {
        string idString = SaveKey(si);

        if (DataHandler.Instance.TryGetShelfItems(idString, out SaveDataWrapper wrapper) && wrapper.items != null)
        {
            Debug.Log($"Loading shelf {idString}'s items from store file.");
            itemsTotalWidth = wrapper.itemsTotalWidth;

            // Copy so later edits to shelfItems don't mutate the stored data; skip removed products.
            shelfItems = new List<RetailItemData>(wrapper.items.Count);
            foreach (RetailItemData itemData in wrapper.items)
            {
                if (itemData == null) continue;

                itemData.prefab = ProductPrefabs.Load(itemData.name);
                if (itemData.prefab == null)
                {
                    Debug.LogWarning($"Shelf {idString}: product '{itemData.name}' no longer exists, skipping.");
                    continue;
                }

                shelfItems.Add(itemData);
            }

            return true;
        }

        Debug.LogWarning($"Shelf {idString}'s items not found in store file — will generate and save.");
        return false;
    }

    
    static string SaveKey(ShelfInfo si) => $"ID{si.shelfId}_{si.subShelfId}_{si.subSubShelfId}";

    GameObject GetRandomProduct(ItemCategory itemCategory)
    {
        string[] categoryIds = itemCategories.Categories[(int)itemCategory].Items;
        if (categoryIds == null || categoryIds.Length == 0) return null;

        return ProductPrefabs.Load(categoryIds[Random.Range(0, categoryIds.Length)]);
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
