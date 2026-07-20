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
    ExperimentalIKHumanoid
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
public class AgentSpawnSaveData
{
    public float posX, posY, posZ;
    public float rotationY;
}

[Serializable]
public class AisleMarkerSaveData
{
    public float posX, posY, posZ;
    public float rotationY;
    public string category1, category2, category3;
    public int aisleNumber;
    public float cableLength;
}

[Serializable]
public class StoreData
{
    public int version = 1;
    public float floorWidth  = 10f;
    public float floorHeight = 10f;
    public float wallHeight  = 3f;
    public List<ShelfSaveData> shelves = new();
    public Dictionary<string, SaveDataWrapper> shelfItems = new();
    public List<SelfCheckoutSaveData> selfCheckoutLocations = new();
    public AgentSpawnSaveData agentSpawnLocation = null;
    public List<AisleMarkerSaveData> aisleMarkerLocations = new();
}

public class DataHandler : MonoBehaviour
{

    public ItemCategories itemCategories;
    public Dictionary<string, ItemPriceData> itemPriceData;
    public static DataHandler Instance { get; private set; }

    public StoreData currentStoreData { get; private set; } = new StoreData();

    [Header("Agent Prefabs")]
    public GameObject agentObject;
    public GameObject ikHumanoidObject;
    public Vector3 AgentPosition => _activeAgentObject != null
        ? _activeAgentObject.transform.position
        : agentObject != null ? agentObject.transform.position : Vector3.zero;
    public Vector3 agentSpawnPosition;
    public AgentAvatarSetting agentAvatarSetting;
    public AgentInteractionStyle agentInteractionStyle;
    public AgentBasketStyle agentBasketStyle;
    public AgentController mainAgentController;

    [Header("Self Checkout")]
    public ScanningDifficulty scanningDifficulty;
    public GameObject selfCheckoutCounter;

    [Header("Agent Spawn Marker")]
    public GameObject agentSpawnMarkerPrefab;

    [Header("Aisle Marker")]
    public GameObject aisleMarkerPrefab;

    [Header("Item Physics")]
    public bool enableShelfItemPhysics;

    [Header("Expiration Date Decals")]
    public Material expirationDateDecalMaterial;

    [Header("Experimental")]
    [Tooltip("If true, ItemSpawner combines each row of identical products into one mesh and " +
             "registers that single combined chunk with the GPU instancer instead of adding each " +
             "product individually. Used for A/B perf comparison.")]
    public bool combineRowMeshes;

    [Header("Store")]
    public string storeName = "DefaultStore";
    [Tooltip("If true, destroy scene shelves on Awake and load from saved JSON")]
    public bool readSave = false;
    public GameObject shelfPrefab;
    public GameObject floor;
    public float CeilingY
    {
        get
        {
            if (floor != null)
            {
                RoomStructure roomStructure = floor.GetComponent<RoomStructure>();
                if (roomStructure != null)
                    return roomStructure.CeilingY;

                return floor.transform.position.y + currentStoreData.wallHeight;
            }

            return currentStoreData.wallHeight;
        }
    }
    
    [Header("Store Builder")]
    public SB_UIHandler uiHandler;
    public SB_InteractionController interactionController;
    public string agentSandboxScene = "AgentSandboxScene";

    [Header("Store Builder")] public bool debugMode = false;
    
    // Persists each shelf's intended spawnItems state by shelfId
    public Dictionary<int, bool> shouldShelfSpawnItems = new();

    private int currentShelfId;
    private GameObject _activeAgentObject;

    void Awake()
    {
        // Assemble Singleton class
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
        ExpirationDateDecalCatalog.Initialize(expirationDateDecalMaterial);

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
        bool isStoreBuilder = SceneManager.GetActiveScene().buildIndex == 0;
        if (!isStoreBuilder && !debugMode) ApplyAvatarSetting();
    }

    void ApplyAvatarSetting()
    {
        if (agentAvatarSetting == AgentAvatarSetting.VR)
        {
            if (agentObject != null)
            {
                // agentObject.SetActive(true);
                // agentObject.transform.position = agentSpawnPosition;
                GameObject go = Instantiate(agentObject, agentSpawnPosition, Quaternion.identity);
                _activeAgentObject = go;
                
                // AgentController to be accessed by ChatUIManager
                mainAgentController = go.GetComponentInChildren<AgentController>();
                WebSocketHandler.Instance?.SetAgent(go.GetComponentInChildren<AgentController>(true));
                Camera cam = go != null ? go.GetComponentInChildren<Camera>() : null;
                if (cam != null)
                {
                    cam.tag = "MainCamera";
                    GPUInstanceTracker.Instance.SetCamera(cam);
                }
            }
            // if (ikHumanoidObject != null) ikHumanoidObject.SetActive(false);
        }
        else
        {
            // if (agentObject != null) agentObject.SetActive(false);
            if (ikHumanoidObject != null)
            {
                // ikHumanoidObject.SetActive(true);
                // ikHumanoidObject.transform.position = agentSpawnPosition;
                GameObject go = Instantiate(ikHumanoidObject, agentSpawnPosition, Quaternion.identity);
                _activeAgentObject = go;
                Camera cam = go != null ? go.GetComponentInChildren<Camera>() : null;
                if (cam != null)
                {
                    cam.tag = "MainCamera";
                    GPUInstanceTracker.Instance.SetCamera(cam);
                }
            }
        }
    }

    public int GetUniqueShelfId()
    {
        currentShelfId++;
        return currentShelfId;
    }

    public void SpawnIntoStore()
    {
        SaveStore();
        SceneManager.sceneLoaded += OnAgentSandboxSceneLoaded;
        SceneManager.LoadScene(agentSandboxScene);
    }

    private void OnAgentSandboxSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        SceneManager.sceneLoaded -= OnAgentSandboxSceneLoaded;

        RoomStructure roomStructure = FindFirstObjectByType<RoomStructure>();
        floor = roomStructure != null ? roomStructure.gameObject : null;

        LoadStore();
        ApplyAvatarSetting();
    }

    public void LoadStore()
    {
        // Selection boxes are separate scene objects, so clear them along with the
        // store objects they wrap before rebuilding the scene.
        foreach (ShelfSelector existing in
                 FindObjectsByType<ShelfSelector>(FindObjectsSortMode.None))
            Destroy(existing.gameObject);

        foreach (PropSelector existing in
                 FindObjectsByType<PropSelector>(FindObjectsSortMode.None))
            Destroy(existing.gameObject);

        // Clear all shelves in the scene
        foreach (ShelfBuilder existing in
                 FindObjectsByType<ShelfBuilder>(FindObjectsSortMode.None))
            Destroy(existing.gameObject);

        // Clear all self-checkout counters
        foreach (SelfCheckoutMarker existing in
                 FindObjectsByType<SelfCheckoutMarker>(FindObjectsSortMode.None))
            Destroy(existing.gameObject);

        // Clear any existing agent spawn marker (only in Store Builder)
        foreach (AgentSpawnMarker existing in
                 FindObjectsByType<AgentSpawnMarker>(FindObjectsSortMode.None))
            Destroy(existing.gameObject);

        // Clear all aisle markers
        foreach (AisleMarker existing in
                 FindObjectsByType<AisleMarker>(FindObjectsSortMode.None))
            Destroy(existing.gameObject);

        string path = Path.Combine(Application.persistentDataPath, storeName + ".json");
        if (!File.Exists(path))
        {
            Debug.LogWarning($"No store file found at {path}");
            return;
        }

        StoreData storeData = JsonConvert.DeserializeObject<StoreData>(File.ReadAllText(path));
        storeData.aisleMarkerLocations ??= new List<AisleMarkerSaveData>();
        currentStoreData = storeData;
        Debug.Log($"Loading store '{storeName}' — {storeData.shelves.Count} shelf(ves).");

        if (floor != null)
        {
            var roomStructure = floor.GetComponent<RoomStructure>();
            if (roomStructure != null)
            {
                roomStructure.wallHeight = storeData.wallHeight;
                roomStructure.SetFloorDimensions(storeData.floorWidth, storeData.floorHeight);
            }
            else
            {
                Vector3 floorScale = floor.transform.localScale;
                floorScale.x = storeData.floorWidth;
                floorScale.z = storeData.floorHeight;
                floor.transform.localScale = floorScale;
            }
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
            builder.InitFromSaveData(data);
            if (sceneName == "StoreBuilder")
            {
                builder.spawnItems = false;
            }
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

        if (storeData.agentSpawnLocation != null)
        {
            AgentSpawnSaveData spawnData = storeData.agentSpawnLocation;
            Vector3 pos = new Vector3(spawnData.posX, spawnData.posY, spawnData.posZ);
            agentSpawnPosition = pos;

            if (sceneName == "StoreBuilder" && agentSpawnMarkerPrefab != null)
            {
                Quaternion rot = Quaternion.Euler(0f, spawnData.rotationY, 0f);
                GameObject go = Instantiate(agentSpawnMarkerPrefab, pos, rot);
                go.AddComponent<AgentSpawnMarker>();
                interactionController.SummonPropSelectorBox(go);
            }
        }

        if (aisleMarkerPrefab != null)
        {
            foreach (AisleMarkerSaveData markerData in storeData.aisleMarkerLocations)
            {
                Vector3 pos = new Vector3(markerData.posX, markerData.posY, markerData.posZ);
                Quaternion rot = Quaternion.Euler(0f, markerData.rotationY, 0f);
                GameObject go = Instantiate(aisleMarkerPrefab, pos, rot);

                AisleMarker marker = go.GetComponent<AisleMarker>();
                if (marker != null)
                {
                    marker.BuildAisleMarker(
                        markerData.category1,
                        markerData.category2,
                        markerData.category3,
                        markerData.aisleNumber,
                        markerData.cableLength
                    );
                }

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
        RoomStructure roomStructure = floor != null ? floor.GetComponent<RoomStructure>() : null;
        StoreData storeData = new StoreData
        {
            shelfItems  = currentStoreData.shelfItems,
            floorWidth  = floor != null ? floor.transform.localScale.x : currentStoreData.floorWidth,
            floorHeight = floor != null ? floor.transform.localScale.z : currentStoreData.floorHeight,
            wallHeight  = roomStructure != null ? roomStructure.wallHeight : currentStoreData.wallHeight
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
                spawnItems            = shouldShelfSpawnItems.TryGetValue(b.shelfId, out bool wantsSpawnItems)
                    ? wantsSpawnItems
                    : b.spawnItems,
                spawnPriceTags        = b.spawnPriceTags,
                itemSpawnOption       = b.itemSpawnOption,
                subShelfCategories    = b.subShelfCategories,
                spawnHingeDoors       = b.isFridge,
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

        AgentSpawnMarker spawnMarker = FindAnyObjectByType<AgentSpawnMarker>();
        if (spawnMarker != null)
        {
            Vector3 pos = spawnMarker.transform.position;
            storeData.agentSpawnLocation = new AgentSpawnSaveData
            {
                posX      = pos.x,
                posY      = pos.y,
                posZ      = pos.z,
                rotationY = spawnMarker.transform.eulerAngles.y
            };
            agentSpawnPosition = pos;
        }

        foreach (AisleMarker marker in FindObjectsByType<AisleMarker>(FindObjectsSortMode.None))
        {
            Vector3 pos = marker.transform.position;
            storeData.aisleMarkerLocations.Add(new AisleMarkerSaveData
            {
                posX        = pos.x,
                posY        = pos.y,
                posZ        = pos.z,
                rotationY   = marker.transform.eulerAngles.y,
                category1   = marker.Category1,
                category2   = marker.Category2,
                category3   = marker.Category3,
                aisleNumber = marker.AisleNumber,
                cableLength = marker.CableLength
            });
        }

        currentStoreData = storeData;
        string path = Path.Combine(Application.persistentDataPath, storeName + ".json");
        File.WriteAllText(path, JsonConvert.SerializeObject(currentStoreData, Formatting.Indented));
        Debug.Log($"Store saved to {path}");
    }

    public void ResetEnvironment()
    {
        ItemPoolingManager.Instance?.ClearPool();
        ShelfBuilder.DeleteAllPriceTags();

        foreach (GameObject obj in GameObject.FindGameObjectsWithTag("RetailItem"))
            Destroy(obj);

        GameObject agent = _activeAgentObject != null ? _activeAgentObject : agentObject;
        if (agent != null)
        {
            agent.transform.position = agentSpawnPosition;
            Rigidbody rb = agent.GetComponentInChildren<Rigidbody>();
            if (rb != null)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }

        LoadStore();
    }
}
