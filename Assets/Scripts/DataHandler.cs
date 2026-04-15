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

    // Item spawning
    public bool            spawnItems;
    public bool            spawnPriceTags;
    public ItemSpawnOption itemSpawnOption;
    public ItemCategory    itemCategory;
}

[System.Serializable]
public class StoreData
{
    public int version = 1;
    public List<ShelfSaveData> shelves = new();
    // Keyed by "{shelfId}_{subShelfId}_{subSubShelfId}"
    public Dictionary<string, SaveDataWrapper> shelfItems = new();
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

    private StoreData _storeData = new StoreData();
    private string StorePath => Path.Combine(Application.persistentDataPath, storeName + ".json");

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

        // Always load the store file into memory so shelf items are accessible
        // regardless of whether readSave is true
        if (File.Exists(StorePath))
            _storeData = JsonConvert.DeserializeObject<StoreData>(File.ReadAllText(StorePath));

        if (readSave) LoadStore();
    }

    public void LoadStore()
    {
        foreach (ShelfBuilder existing in FindObjectsByType<ShelfBuilder>(FindObjectsSortMode.None))
            Destroy(existing.gameObject);

        if (_storeData.shelves.Count == 0)
        {
            Debug.LogWarning($"No shelf data in store '{storeName}'.");
            return;
        }

        Debug.Log($"Loading store '{storeName}' — {_storeData.shelves.Count} shelf(ves).");

        for (int i = 0; i < _storeData.shelves.Count; i++)
        {
            ShelfSaveData data = _storeData.shelves[i];
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
        _storeData.shelves.Clear();

        foreach (ShelfBuilder b in FindObjectsByType<ShelfBuilder>(FindObjectsSortMode.None))
        {
            Vector3 pos = b.transform.position;
            _storeData.shelves.Add(new ShelfSaveData
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
            });
        }

        WriteStoreFile();
    }

    // Called by ShelfItemData to persist generated items into the store file
    public void SaveShelfItems(ShelfInfo si, SaveDataWrapper data)
    {
        _storeData.shelfItems[ShelfKey(si)] = data;
        WriteStoreFile();
    }

    // Called by ShelfItemData when ItemSpawnOption is ReadFromSave
    public SaveDataWrapper LoadShelfItems(ShelfInfo si)
    {
        string key = ShelfKey(si);
        if (_storeData.shelfItems.TryGetValue(key, out SaveDataWrapper data))
            return data;

        Debug.LogError($"No saved items found for shelf {key}");
        return null;
    }

    private void WriteStoreFile()
    {
        File.WriteAllText(StorePath, JsonConvert.SerializeObject(_storeData, Formatting.Indented));
        Debug.Log($"Store saved to {StorePath}");
    }

    private static string ShelfKey(ShelfInfo si) => $"{si.shelfId}_{si.subShelfId}_{si.subSubShelfId}";
}
