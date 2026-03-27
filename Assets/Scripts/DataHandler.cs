using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;

[System.Serializable]
public class ItemCategories
{
    public ItemCategoryData[] Categories;
}

[System.Serializable]
public struct ShelfConfiguration
{
    public bool buildShelves;
    public bool buildBackWall;
    public bool buildShelfRoof;
}

[System.Serializable]
public class ItemCategoryData
{
    public string Category;
    public string[] Items;
}

[System.Serializable]
public struct ItemPriceData
{
    public string netWeight;
    public float pricePHP;
    public string allergens;
    public string possibleAllergens;
    public string nutritionalFacts;
}

public enum ItemCategory
{
    Water,
    Soda,
    Juice,
    Dairies,
    Liquor,
    Biscuit,
    Can,
    Chips,
    Nuts,
    Soup,
    Noodles
}

public enum ItemSpawnOption
{
    GenerateRandom,
    GenerateRandomThenSave,
    ReadFromSave
}

[System.Serializable]
public struct ShelfInfo
{
    public int shelfId;
    public int subShelfId;
    public int subSubShelfId;
}

// TODO: getCategoryIndexFromName, itemTags.json

public class DataHandler : MonoBehaviour
{

    public ItemCategories itemCategories;
    public Dictionary<string, ItemPriceData> itemPriceData;
    public static DataHandler Instance { get; private set; }
    
    void Awake()
    {
        // Assemble Singleton class
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        
        Debug.Log("Loading item categories...");
        TextAsset categoriesJson = Resources.Load<TextAsset>("Data/Categories");
        itemCategories = JsonUtility.FromJson<ItemCategories>(categoriesJson.text);
        Debug.Log($"Done. Loaded {itemCategories.Categories.Length} categories.");
        
        Debug.Log("Loading item price data...");
        TextAsset priceDataText = Resources.Load<TextAsset>("Data/PriceData");
        itemPriceData = JsonConvert.DeserializeObject<Dictionary<string, ItemPriceData>>(priceDataText.text);
        Debug.Log($"Done. Loaded data of {itemPriceData.Keys.Count} items.");
    }
}
