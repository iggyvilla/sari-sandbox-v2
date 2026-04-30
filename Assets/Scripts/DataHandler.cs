using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json;
using UnityEngine.SceneManagement;

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

public enum AgentInteractionStyle
{
    Gaze,
    Manual,
    ManualButGazeDoor
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
    // Shelf ID
    public int shelfId;
    
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
    public Dictionary<string, SaveDataWrapper> shelfItems = new();
}

public class DataHandler : MonoBehaviour
{

    public ItemCategories itemCategories;
    public Dictionary<string, ItemPriceData> itemPriceData;
    public static DataHandler Instance { get; private set; }

    public StoreData currentStoreData { get; private set; } = new StoreData();

    [Header("Agent")]
    public GameObject agentObject;
    public Vector3 AgentPosition => agentObject != null ? agentObject.transform.position : Vector3.zero;
    public AgentInteractionStyle agentInteractionStyle;

    [Header("Store")]
    public string storeName = "DefaultStore";
    [Tooltip("If true, destroy scene shelves on Awake and load from saved JSON")]
    public bool readSave = false;
    public GameObject shelfPrefab;
    public GameObject floor;
    
    [Header("Store Builder")]
    public SB_UIHandler uiHandler;
    public SB_InteractionController interactionController;

    // Persists each shelf's intended spawnItems state by shelfId
    public Dictionary<int, bool> shouldShelfSpawnItems = new();

    private int currentShelfId = 0;

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

    public int GetUniqueShelfId()
    {
        currentShelfId++;
        return currentShelfId;
    }

    public void LoadStore()
    {
        // Clear all shelves in the scene
        foreach (ShelfBuilder existing in 
                 FindObjectsByType<ShelfBuilder>(FindObjectsSortMode.None))
            Destroy(existing.gameObject);

        string path = Path.Combine(Application.persistentDataPath, storeName + ".json");
        if (!File.Exists(path))
        {
            Debug.LogWarning($"No store file found at {path}");
            return;
        }

        StoreData storeData = JsonConvert.DeserializeObject<StoreData>(File.ReadAllText(path));
        currentStoreData = storeData;
        Debug.Log($"Loading store '{storeName}' — {storeData.shelves.Count} shelf(ves).");

        shouldShelfSpawnItems.Clear();
        string sceneName = SceneManager.GetActiveScene().name;
        
        foreach (ShelfSaveData data in storeData.shelves)
        {
            shouldShelfSpawnItems[data.shelfId] = data.spawnItems;
            currentShelfId = Math.Max(currentShelfId, data.shelfId);

            Vector3 pos   = new Vector3(data.posX, data.posY, data.posZ);
            GameObject go = Instantiate(shelfPrefab, pos, Quaternion.identity);

            ShelfBuilder builder = go.GetComponent<ShelfBuilder>();
            builder.floor      = floor;
            builder.spawnItems = false;
            builder.InitFromSaveData(data);
            builder.Rebuild();
            
            if (sceneName == "StoreBuilder")
            {
                builder.SummonOutlineBox(
                    uiHandler, 
                    interactionController
                );
            }
        }
    }

    public void SaveShelfItems(string idString, SaveDataWrapper data)
    {
        currentStoreData.shelfItems[idString] = data;
        string path = Path.Combine(Application.persistentDataPath, storeName + ".json");
        File.WriteAllText(path, JsonConvert.SerializeObject(currentStoreData, Formatting.Indented));
        Debug.Log($"Saved shelf items for {idString} to {path}");
    }

    public bool TryGetShelfItems(string idString, out SaveDataWrapper data)
    {
        return currentStoreData.shelfItems.TryGetValue(idString, out data);
    }

    public void SaveStore()
    {
        ShelfBuilder[] builders = FindObjectsByType<ShelfBuilder>(FindObjectsSortMode.None);
        StoreData storeData = new StoreData
        {
            shelfItems = currentStoreData.shelfItems
        };

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
                shelfId               = b.shelfId
            });
        }

        currentStoreData = storeData;
        string path = Path.Combine(Application.persistentDataPath, storeName + ".json");
        File.WriteAllText(path, JsonConvert.SerializeObject(currentStoreData, Formatting.Indented));
        Debug.Log($"Store saved to {path}");
    }
}
