using System.Collections.Generic;
using System.IO;
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

public enum FridgeDoorStyle
{
    Single,
    Double
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

[System.Serializable]
public class ShelfSaveData
{
    // World position
    public float posX, posY, posZ;

    // Shelf Dimensions
    public float shelfWidth;
    public float shelfBootHeight;
    public int   shelfLevels;
    public float distanceBetweenLevels;
    public float rotationY;
    public float shelfRoofHeight;

    // Configurations
    public ShelfConfiguration frontShelfConfig;
    public ShelfConfiguration backShelfConfig;
    public ShelfConfiguration leftShelfConfig;
    public ShelfConfiguration rightShelfConfig;
    public FridgeDoorStyle    fridgeDoorStyle;

    // Item spawning
    public bool            spawnItems;
    public bool            spawnPriceTags;
    public bool            spawnHingeDoors;
    public ItemSpawnOption itemSpawnOption;
    public ItemCategory    itemCategory;
}

[System.Serializable]
public class StoreData
{
    public int version = 1;
    public List<ShelfSaveData> shelves = new();
}

public class DataHandler : MonoBehaviour
{

    public ItemCategories itemCategories;
    public Dictionary<string, ItemPriceData> itemPriceData;
    public static DataHandler Instance { get; private set; }

    [Header("Store")]
    public string storeName = "DefaultStore";
    [Tooltip("If true, destroy scene shelves on Awake and load from saved JSON")]
    public bool readSave = false;
    public GameObject shelfPrefab;
    public GameObject floor;

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

        if (readSave)
        {
            LoadStore();
        }
        else
        {
            SaveStore();
        }
    }

    public void LoadStore()
    {
        if (!readSave) return;

        foreach (ShelfBuilder existing in FindObjectsByType<ShelfBuilder>(FindObjectsSortMode.None))
            Destroy(existing.gameObject);

        string path = Path.Combine(Application.persistentDataPath, storeName + ".json");
        if (!File.Exists(path))
        {
            Debug.LogWarning($"No store file found at {path}");
            return;
        }

        StoreData storeData = JsonConvert.DeserializeObject<StoreData>(File.ReadAllText(path));
        Debug.Log($"Loading store '{storeName}' — {storeData.shelves.Count} shelf(ves).");

        for (int i = 0; i < storeData.shelves.Count; i++)
        {
            ShelfSaveData data = storeData.shelves[i];
            Vector3 pos = new Vector3(data.posX, data.posY, data.posZ);
            GameObject go = Instantiate(shelfPrefab, pos, Quaternion.identity);

            ShelfBuilder builder = go.GetComponent<ShelfBuilder>();
            builder.shelfId = i;
            builder.floor   = floor;
            builder.InitFromSaveData(data);
            // Start() fires next frame and calls BuildRectangularShelf()
        }
    }

    public void SaveStore()
    {
        ShelfBuilder[] builders = FindObjectsByType<ShelfBuilder>(FindObjectsSortMode.None);
        StoreData storeData = new StoreData();

        foreach (ShelfBuilder b in builders)
        {
            Vector3 pos = b.transform.position;
            storeData.shelves.Add(new ShelfSaveData
            {
                posX                  = pos.x,
                posY                  = pos.y,
                posZ                  = pos.z,
                shelfWidth            = b.shelfWidth,
                shelfBootHeight       = b.shelfBootHeight,
                shelfLevels           = b.shelfLevels,
                distanceBetweenLevels = b.distanceBetweenLevels,
                rotationY             = b.rotationY,
                shelfRoofHeight       = b.shelfRoofHeight,
                frontShelfConfig      = b.frontShelfConfig,
                backShelfConfig       = b.backShelfConfig,
                leftShelfConfig       = b.leftShelfConfig,
                rightShelfConfig      = b.rightShelfConfig,
                spawnItems            = b.spawnItems,
                spawnPriceTags        = b.spawnPriceTags,
                itemSpawnOption       = b.itemSpawnOption,
                itemCategory          = b.itemCategory,
                spawnHingeDoors       = b.spawnHingeDoors,
                fridgeDoorStyle       = b.fridgeDoorStyle,
            });
        }

        string path = Path.Combine(Application.persistentDataPath, storeName + ".json");
        File.WriteAllText(path, JsonConvert.SerializeObject(storeData, Formatting.Indented));
        Debug.Log($"Store saved to {path}");
    }
}
