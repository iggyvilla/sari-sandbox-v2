using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json;
using UnityEngine.SceneManagement;

[Serializable]
public class ItemCategories
{
    public ItemCategoryData[] Categories;
}

[Serializable]
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

public enum AgentBasketStyle
{
    None,
    LeftHand
}

public enum ScanningDifficulty
{
    Easy,
    Medium,
    Hard
}

public enum AgentAvatarSetting
{
    VR,
    IKHumanoid
}

[Serializable]
public class ItemCategoryData
{
    public string Category;
    public string[] Items;
}

[Serializable]
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

[Serializable]
public struct ShelfInfo
{
    public int shelfId;
    public int subShelfId;
    public int subSubShelfId;
}

// TODO: getCategoryIndexFromName, itemTags.json

[Serializable]
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
    public Dictionary<string, ItemCategory> subShelfCategories = new();
}

[Serializable]
public class SelfCheckoutSaveData
{
    public float posX, posY, posZ;
    public float rotationY;
}

[Serializable]
public class StoreData
{
    public int version = 1;
    public float floorWidth  = 10f;
    public float floorHeight = 10f;
    public List<ShelfSaveData> shelves = new();
    public Dictionary<string, SaveDataWrapper> shelfItems = new();
    public List<SelfCheckoutSaveData> selfCheckoutLocations = new();
}

public class DataHandler : MonoBehaviour
{

    public ItemCategories itemCategories;
    public Dictionary<string, ItemPriceData> itemPriceData;
    public static DataHandler Instance { get; private set; }

    public StoreData currentStoreData { get; private set; } = new StoreData();

    [Header("Agent")]
    public GameObject agentObject;
    public GameObject ikHumanoidObject;
    public Vector3 AgentPosition => agentObject != null ? agentObject.transform.position : Vector3.zero;
    public Vector3 agentSpawnPosition;
    public AgentAvatarSetting agentAvatarSetting;
    public AgentInteractionStyle agentInteractionStyle;
    public AgentBasketStyle agentBasketStyle;

    [Header("Self Checkout")]
    public ScanningDifficulty scanningDifficulty;
    public GameObject selfCheckoutCounter;

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

    void Start()
    {
        ApplyAvatarSetting();
    }

    void ApplyAvatarSetting()
    {
        if (agentAvatarSetting == AgentAvatarSetting.VR)
        {
            if (agentObject != null)
            {
                agentObject.SetActive(true);
                agentObject.transform.position = agentSpawnPosition;
            }
            if (ikHumanoidObject != null) ikHumanoidObject.SetActive(false);

            Camera cam = agentObject != null ? agentObject.GetComponentInChildren<Camera>() : null;
            if (cam != null) GPUInstanceTracker.Instance.SetCamera(cam);
        }
        else
        {
            if (agentObject != null) agentObject.SetActive(false);
            if (ikHumanoidObject != null)
            {
                ikHumanoidObject.SetActive(true);
                ikHumanoidObject.transform.position = agentSpawnPosition;
            }

            Camera cam = ikHumanoidObject != null ? ikHumanoidObject.GetComponentInChildren<Camera>() : null;
            if (cam != null) GPUInstanceTracker.Instance.SetCamera(cam);
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

        // Clear all self-checkout counters
        foreach (SelfCheckoutMarker existing in
                 FindObjectsByType<SelfCheckoutMarker>(FindObjectsSortMode.None))
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

        if (floor != null)
        {
            Vector3 floorScale = floor.transform.localScale;
            floorScale.x = storeData.floorWidth;
            floorScale.z = storeData.floorHeight;
            floor.transform.localScale = floorScale;
        }

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

        if (selfCheckoutCounter != null)
        {
            foreach (SelfCheckoutSaveData scData in storeData.selfCheckoutLocations)
            {
                Vector3 pos = new Vector3(scData.posX, scData.posY, scData.posZ);
                Quaternion rot = Quaternion.Euler(0f, scData.rotationY, 0f);
                GameObject go = Instantiate(selfCheckoutCounter, pos, rot);
                go.AddComponent<SelfCheckoutMarker>();

                if (sceneName == "StoreBuilder")
                    interactionController.SummonPropSelectorBox(go);
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
            shelfItems   = currentStoreData.shelfItems,
            floorWidth   = floor != null ? floor.transform.localScale.x : currentStoreData.floorWidth,
            floorHeight  = floor != null ? floor.transform.localScale.z : currentStoreData.floorHeight
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
                subShelfCategories    = b.subShelfCategories,
                spawnHingeDoors       = b.spawnHingeDoors,
                fridgeDoorStyle       = b.fridgeDoorStyle,
                shelfId               = b.shelfId
            });
        }

        foreach (SelfCheckoutMarker sc in FindObjectsByType<SelfCheckoutMarker>(FindObjectsSortMode.None))
        {
            Vector3 pos = sc.transform.position;
            storeData.selfCheckoutLocations.Add(new SelfCheckoutSaveData
            {
                posX      = pos.x,
                posY      = pos.y,
                posZ      = pos.z,
                rotationY = sc.transform.eulerAngles.y
            });
        }

        currentStoreData = storeData;
        string path = Path.Combine(Application.persistentDataPath, storeName + ".json");
        File.WriteAllText(path, JsonConvert.SerializeObject(currentStoreData, Formatting.Indented));
        Debug.Log($"Store saved to {path}");
    }

    public void ResetEnvironment()
    {
        foreach (GameObject obj in GameObject.FindGameObjectsWithTag("RetailItem"))
            Destroy(obj);

        if (agentObject != null)
        {
            agentObject.transform.position = agentSpawnPosition;
            Rigidbody rb = agentObject.GetComponentInChildren<Rigidbody>();
            if (rb != null)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }

        LoadStore();
    }
}
