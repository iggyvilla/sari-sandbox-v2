using System.Collections.Generic;
using UnityEngine;

public class RetailItemData
{
    public GameObject prefab;
    public string name;
    public List<string> tags;
    public ItemCategoryType itemCategory;
    public RetailItemDimensions dimensions;
    public List<DrawData> itemLocations;
}

public class RetailItemDimensions
{
    public float depth;
    public float width;
    public float height;
}

public class ShelfItemData : MonoBehaviour
{
    public List<RetailItemData> shelfItems = new();
    public float itemTotalWidth = 0f;
    private ItemCategories itemCategories;
    
    void Start()
    {
        while (itemCategories == null)
        {
            itemCategories = DataHandler.Instance.itemCategories;
        }
    }

    public void RandomFillWithCategory(ItemCategoryType itemCategory, float interItemPadding, float widthBudget)
    {
        float lengthwiseOffset = 0.0f;
        bool firstItem = true;

        while (lengthwiseOffset < widthBudget)
        {
            GameObject product = GetRandomProduct(itemCategory);
            
            MeshRenderer r = product.GetComponentInChildren<MeshRenderer>();
            if (r is null)
            {
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
            
            lengthwiseOffset += dimensions.width/2 + (!firstItem ? interItemPadding : 0);
            
            // If the item we're about to spawn won't fit anymore, don't bother
            if (lengthwiseOffset + dimensions.width/2 + interItemPadding > widthBudget)
            {
                itemTotalWidth = lengthwiseOffset;
                break;
            }
            
            // If it does fit within the shelf, add to the item list
            shelfItems.Add(retailItemData);

            lengthwiseOffset += dimensions.width / 2;
            firstItem = false;
        }
    }

    public void LoadItemsFromJson() {}

    
    GameObject GetRandomProduct(ItemCategoryType itemCategory)
    {
        string[] categoryIds = itemCategories.Categories[(int)itemCategory].Items;
        string chosenId = categoryIds[Random.Range(0, categoryIds.Length)];
        
        return Resources.Load<GameObject>("Prefabs/Products/" + chosenId);
    }

}
