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
    [NonSerialized, JsonIgnore]
    public List<string> tags;
    [NonSerialized, JsonIgnore]
    public List<DrawData> itemLocations;
    
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
    
    void Awake()
    {
        while (itemCategories == null)
        {
            itemCategories = DataHandler.Instance.itemCategories;
        }
    }

    public void RandomFillFromCategory(ItemCategory itemCategory, float interItemPadding, float widthBudget)
    {
        float lengthwiseOffset = 0.0f;
        bool firstItem = true;

        while (lengthwiseOffset < widthBudget)
        {
            GameObject product = null;
            
            while (product is null)
            {
                product = GetRandomProduct(itemCategory);
            }
            
            MeshRenderer r = product.GetComponentInChildren<MeshRenderer>();
            if (r is null)
            {
                Debug.LogError(product.name + " has no mesh renderer");
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
                tags = new List<string>(),
                itemCategory = itemCategory,
                dimensions = dimensions,
                itemLocations = new List<DrawData>()
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

        string idString = $"ID{si.shelfId}_{si.subShelfId}_{si.subSubShelfId}";

        SaveDataWrapper wrapper = new SaveDataWrapper
        {
            items = shelfItems,
            itemsTotalWidth = itemsTotalWidth
        };

        DataHandler.Instance.SaveShelfItems(idString, wrapper);
        Debug.Log($"Saved shelf {idString}'s items to store file.");
    }

    public void LoadItemsFromJson(ShelfInfo si)
    {
        string idString = $"ID{si.shelfId}_{si.subShelfId}_{si.subSubShelfId}";

        if (DataHandler.Instance.TryGetShelfItems(idString, out SaveDataWrapper wrapper))
        {
            Debug.Log($"Loading shelf {idString}'s items from store file.");
            itemsTotalWidth = wrapper.itemsTotalWidth;

            foreach (RetailItemData itemData in wrapper.items)
            {
                itemData.prefab = Resources.Load<GameObject>("Prefabs/Products/" + itemData.name);
            }

            shelfItems = wrapper.items;
        }
        else
        {
            Debug.LogError($"Shelf {idString}'s items not found in store file.");
        }
    }

    
    GameObject GetRandomProduct(ItemCategory itemCategory)
    {
        string[] categoryIds = itemCategories.Categories[(int)itemCategory].Items;
        string chosenId = categoryIds[Random.Range(0, categoryIds.Length)];
        
        return Resources.Load<GameObject>("Prefabs/Products/" + chosenId);
    }

}
