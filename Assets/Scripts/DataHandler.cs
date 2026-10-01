using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
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

/// <summary>Position and yaw of a placed store prop.</summary>
[Serializable]
public class PropSaveData
{
    public float posX, posY, posZ;
    public float rotationY;

    [JsonIgnore] public Vector3 Position => new(posX, posY, posZ);
    [JsonIgnore] public Quaternion Rotation => Quaternion.Euler(0f, rotationY, 0f);

    public static T From<T>(Transform transform) where T : PropSaveData, new()
    {
        Vector3 pos = transform.position;
        return new T { posX = pos.x, posY = pos.y, posZ = pos.z, rotationY = transform.eulerAngles.y };
    }
}

[Serializable]
public class SelfCheckoutSaveData : PropSaveData { }

[Serializable]
public class AgentSpawnSaveData : PropSaveData { }

[Serializable]
public class AisleMarkerSaveData : PropSaveData
{
    public string category1, category2, category3;
    public int aisleNumber;
    public float cableLength;
}

[Serializable]
public class StoreData
{
    // v2: enums saved as names; shelf items saved as names only.
    public int version = 2;
    public float floorWidth  = 10f;
    public float floorHeight = 10f;
    public float wallHeight  = 3f;
    // Missing in older saves: keeps the front / middle / Door G defaults (and a seed derived from the store name).
    public StreetSettings street = new();
    // Missing in older saves: no emergency exits.
    public List<WallCell> emergencyExits = new();
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

    // Enums are written as names so store files stay readable; ints from older files still load.
    public static readonly JsonSerializerSettings JsonSettings = new() { Converters = { new StringEnumConverter() } };

    public StoreData currentStoreData { get; private set; } = new StoreData();

    /// <summary>
    /// False when the last <see cref="LoadStore"/> found no store JSON on disk. A sandbox in that
    /// state is serving an empty room, so the benchmark fleet must not advertise it as usable.
    /// </summary>
    public bool StoreLoaded { get; private set; }

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
    public AgentControllerBase mainAgentController;

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
    [Tooltip("Prefab for shelves saved with hinge doors; falls back to shelfPrefab when unset")]
    public GameObject fridgePrefab;
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

    [Header("Debug")] public bool debugMode = false;
    
    // Persists each shelf's intended spawnItems state by shelfId
    public Dictionary<int, bool> shouldShelfSpawnItems = new();

    private const string StoreBuilderSceneName = "StoreBuilder";

    private int currentShelfId;
    private bool _shelfItemsDirty;
    // Set once this session has saved or loaded the store file, so batched item writes never clobber a file it never read.
    private bool _hasStoreFile;
    private GameObject _activeAgentObject;

    public static bool IsStoreBuilderScene => SceneManager.GetActiveScene().name == StoreBuilderSceneName;

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
        // Read before DontDestroyOnLoad moves this object out of its scene.
        bool inStoreBuilder = gameObject.scene.name == StoreBuilderSceneName;
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
            LoadStore();
        // The builder starts empty, so snapshotting it would overwrite the named store file.
        else if (!inStoreBuilder)
            SaveStore();
    }

    void Start()
    {
        if (!IsStoreBuilderScene && !debugMode) ApplyAvatarSetting();
    }

    void ApplyAvatarSetting()
    {
        GameObject prefab = agentAvatarSetting == AgentAvatarSetting.VR ? agentObject : ikHumanoidObject;
        if (prefab == null) return;

        _activeAgentObject = Instantiate(prefab, agentSpawnPosition, Quaternion.identity);

        // Accessed by ChatUIManager; only the VR controller is socket-controllable.
        mainAgentController = _activeAgentObject.GetComponentInChildren<AgentControllerBase>(true);
        if (mainAgentController is AgentController vrController && WebSocketHandler.Instance != null)
            WebSocketHandler.Instance.SetAgent(vrController);

        Camera cam = _activeAgentObject.GetComponentInChildren<Camera>();
        if (cam != null)
        {
            cam.tag = "MainCamera";
            GPUInstanceTracker.Instance.SetCamera(cam);
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
        // Selection boxes wrap store objects, so clear them too before rebuilding the scene.
        DestroyAll<OutlineSelector>();
        DestroyAll<ShelfBuilder>();
        DestroyAll<SelfCheckoutMarker>();
        DestroyAll<AgentSpawnMarker>();
        DestroyAll<AisleMarker>();

        string path = StorePath;
        if (!File.Exists(path))
        {
            // A machine provisioned without its store file would otherwise silently serve an empty
            // room and report itself healthy to the benchmark coordinator. Fail loudly instead.
            StoreLoaded = false;
            Debug.LogError($"No store file found at {path}");
            return;
        }

        StoreData storeData = JsonConvert.DeserializeObject<StoreData>(File.ReadAllText(path), JsonSettings);
        storeData.shelves ??= new List<ShelfSaveData>();
        storeData.shelfItems ??= new Dictionary<string, SaveDataWrapper>();
        storeData.selfCheckoutLocations ??= new List<SelfCheckoutSaveData>();
        storeData.aisleMarkerLocations ??= new List<AisleMarkerSaveData>();
        storeData.street ??= new StreetSettings();
        if (storeData.street.seed == 0) storeData.street.seed = StreetSettings.SeedFor(storeName);
        storeData.emergencyExits ??= new List<WallCell>();
        currentStoreData = storeData;
        Debug.Log($"Loading store '{storeName}' — {storeData.shelves.Count} shelf(ves).");

        ApplyStoreDimensions(storeData.floorWidth, storeData.floorHeight, storeData.wallHeight, storeData.street, storeData.emergencyExits);

        shouldShelfSpawnItems.Clear();
        bool isStoreBuilder = IsStoreBuilderScene;
        
        foreach (ShelfSaveData data in storeData.shelves)
        {
            shouldShelfSpawnItems[data.shelfId] = data.spawnItems;
            currentShelfId = Math.Max(currentShelfId, data.shelfId);

            Vector3 pos   = new Vector3(data.posX, data.posY, data.posZ);
            GameObject prefab = data.spawnHingeDoors && fridgePrefab != null ? fridgePrefab : shelfPrefab;
            GameObject go = Instantiate(prefab, pos, Quaternion.identity);

            ShelfBuilder builder = go.GetComponent<ShelfBuilder>();
            builder.floor      = floor;
            builder.InitFromSaveData(data);
            if (isStoreBuilder)
            {
                builder.spawnItems = false;
            }
            builder.Rebuild();
            AddSelectorInBuilder(go);
        }

        if (selfCheckoutCounter != null)
        {
            foreach (SelfCheckoutSaveData scData in storeData.selfCheckoutLocations)
                AddSelectorInBuilder(Instantiate(selfCheckoutCounter, scData.Position, scData.Rotation));
        }

        if (storeData.agentSpawnLocation != null)
        {
            AgentSpawnSaveData spawnData = storeData.agentSpawnLocation;
            agentSpawnPosition = spawnData.Position;

            if (isStoreBuilder && agentSpawnMarkerPrefab != null)
                AddSelectorInBuilder(Instantiate(agentSpawnMarkerPrefab, spawnData.Position, spawnData.Rotation));
        }

        if (aisleMarkerPrefab != null)
        {
            foreach (AisleMarkerSaveData markerData in storeData.aisleMarkerLocations)
            {
                GameObject go = Instantiate(aisleMarkerPrefab, markerData.Position, markerData.Rotation);
                if (go.TryGetComponent(out AisleMarker marker))
                {
                    marker.BuildAisleMarker(
                        markerData.category1,
                        markerData.category2,
                        markerData.category3,
                        markerData.aisleNumber,
                        markerData.cableLength
                    );
                }

                AddSelectorInBuilder(go);
            }
        }

        StoreLoaded = true;
        _hasStoreFile = true;
    }

    private void AddSelectorInBuilder(GameObject go)
    {
        if (IsStoreBuilderScene) interactionController.SummonSelectorBox(go);
    }

    public float WallHeight => floor != null && floor.TryGetComponent(out RoomStructure room)
        ? room.wallHeight
        : currentStoreData.wallHeight;

    /// <summary>The floor's wall builder, if it has one.</summary>
    public RoomStructure Room => floor != null && floor.TryGetComponent(out RoomStructure room) ? room : null;

    /// <summary>The street wall / exit door setup (a copy: edit it, then pass it to <see cref="ApplyStreetSettings"/>).</summary>
    public StreetSettings Street => (Room != null ? Room.street : currentStoreData.street).Clone();

    /// <summary>Emergency exits on the walls (the saved list while there is no wall builder).</summary>
    public List<WallCell> EmergencyExits => Room != null ? Room.EmergencyExits : currentStoreData.emergencyExits;

    public void ApplyStreetSettings(StreetSettings street)
    {
        if (Room != null) Room.SetStreet(street);
        else currentStoreData.street = street.Clone();
    }

    /// <summary>Resizes the store; null <paramref name="street"/> / <paramref name="exits"/> keep the current ones.</summary>
    public void ApplyStoreDimensions(float width, float depth, float wallHeight, StreetSettings street = null, List<WallCell> exits = null)
    {
        if (floor == null) return;

        RoomStructure room = Room;
        if (room != null)
        {
            room.wallHeight = wallHeight;
            if (street != null) room.street = street.Clone();
            room.SetFloorDimensions(width, depth, exits);
        }
        else
        {
            floor.transform.localScale = new Vector3(width, floor.transform.localScale.y, depth);
        }

        // Wall height may have changed - re-glue every aisle marker to the new ceiling.
        foreach (AisleMarker marker in FindObjectsByType<AisleMarker>(FindObjectsSortMode.None))
            marker.RefreshHeight();
    }

    /// <summary>Gives a duplicated shelf the source shelf's spawn flag and saved items.</summary>
    public void CopyShelfData(int fromId, int toId)
    {
        if (shouldShelfSpawnItems.TryGetValue(fromId, out bool spawn))
            shouldShelfSpawnItems[toId] = spawn;

        string from = ShelfItemData.KeyPrefix(fromId);
        string to = ShelfItemData.KeyPrefix(toId);
        foreach (var kvp in new List<KeyValuePair<string, SaveDataWrapper>>(currentStoreData.shelfItems))
        {
            if (kvp.Key.StartsWith(from, StringComparison.Ordinal))
                currentStoreData.shelfItems[to + kvp.Key.Substring(from.Length)] = kvp.Value;
        }
    }

    private static void DestroyAll<T>() where T : Component
    {
        foreach (T existing in FindObjectsByType<T>(FindObjectsSortMode.None))
            Destroy(existing.gameObject);
    }

    // Batched: every shelf that generates items this frame is written in one LateUpdate.
    public void SaveShelfItems(string idString, SaveDataWrapper data)
    {
        currentStoreData.shelfItems[idString] = data;
        _shelfItemsDirty = true;
    }

    void LateUpdate()
    {
        if (_shelfItemsDirty && _hasStoreFile) WriteStoreFile();
    }

    private void WriteStoreFile()
    {
        _shelfItemsDirty = false;
        string path = StorePath;
        File.WriteAllText(path, JsonConvert.SerializeObject(currentStoreData, Formatting.Indented, JsonSettings));
        Debug.Log($"Store saved to {path}");
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
            shelfItems  = ShelfItemsFor(builders),
            floorWidth  = floor != null ? floor.transform.localScale.x : currentStoreData.floorWidth,
            floorHeight = floor != null ? floor.transform.localScale.z : currentStoreData.floorHeight,
            wallHeight  = WallHeight,
            street      = Street,
            emergencyExits = EmergencyExits
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
            storeData.selfCheckoutLocations.Add(PropSaveData.From<SelfCheckoutSaveData>(sc.transform));

        AgentSpawnMarker spawnMarker = FindAnyObjectByType<AgentSpawnMarker>();
        if (spawnMarker != null)
        {
            storeData.agentSpawnLocation = PropSaveData.From<AgentSpawnSaveData>(spawnMarker.transform);
            agentSpawnPosition = spawnMarker.transform.position;
        }

        foreach (AisleMarker marker in FindObjectsByType<AisleMarker>(FindObjectsSortMode.None))
        {
            AisleMarkerSaveData data = PropSaveData.From<AisleMarkerSaveData>(marker.transform);
            data.category1   = marker.Category1;
            data.category2   = marker.Category2;
            data.category3   = marker.Category3;
            data.aisleNumber = marker.AisleNumber;
            data.cableLength = marker.CableLength;
            storeData.aisleMarkerLocations.Add(data);
        }

        currentStoreData = storeData;
        _hasStoreFile = true;
        WriteStoreFile();
    }

    // Saved items of shelves that still exist, so deleted shelves don't leave stale entries.
    private Dictionary<string, SaveDataWrapper> ShelfItemsFor(ShelfBuilder[] builders)
    {
        Dictionary<string, SaveDataWrapper> kept = new();
        foreach (ShelfBuilder b in builders)
        {
            string prefix = ShelfItemData.KeyPrefix(b.shelfId);
            foreach (var kvp in currentStoreData.shelfItems)
                if (kvp.Key.StartsWith(prefix, StringComparison.Ordinal)) kept[kvp.Key] = kvp.Value;
        }

        return kept;
    }

    /// <summary>
    /// Restores the environment to its pristine state and does not return until it has settled.
    ///
    /// Unity defers Destroy() to the end of the frame and freshly spawned items fall under physics
    /// for up to a couple of seconds afterwards, so every benchmark attempt waits on this coroutine
    /// and no state leaks between tries.
    ///
    /// <paramref name="agentYawDegrees"/> optionally picks the facing the agent is left in, so a
    /// run can start pointed at a particular aisle instead of the default zero heading.
    /// </summary>
    public IEnumerator ResetEnvironmentRoutine(float? agentYawDegrees = null)
    {
        Debug.Log("ResetEnvironment: clearing pooled and runtime items.");
        ItemPoolingManager.Instance?.ClearPool();
        ShelfBuilder.DeleteAllPriceTags();

        foreach (GameObject obj in GameObject.FindGameObjectsWithTag("RetailItem"))
            Destroy(obj);

        // Let Unity's end-of-frame destruction pass actually run before LoadStore() repopulates.
        // Without this the old items are still alive and are picked up by the FindObjectsByType
        // sweeps below, and the item pool re-registers objects that are about to disappear.
        yield return null;

        Debug.Log("ResetEnvironment: resetting agent and rebuilding store.");
        ResetAgentState(agentYawDegrees);
        ChatUIManager.Instance?.ClearLog();

        LoadStore();

        Debug.Log("ResetEnvironment: waiting for spawned items to settle.");
        yield return WaitForItemsToSettle();
        Debug.Log("ResetEnvironment: settled.");
    }

    /// <summary>
    /// Returns the agent to its spawn pose and clears carried state: facing, grip, hand pose,
    /// pointing mode, and whether the basket is held up in view.
    ///
    /// <paramref name="agentYawDegrees"/> is the absolute world heading the agent is left facing,
    /// not a delta: 90 always means the same direction however the agent was turned beforehand.
    /// Null keeps the zero heading resets have always used.
    /// </summary>
    private void ResetAgentState(float? agentYawDegrees = null)
    {
        GameObject agent = _activeAgentObject != null ? _activeAgentObject : agentObject;
        if (agent == null) return;

        agent.transform.position = agentSpawnPosition;
        agent.transform.rotation = Quaternion.identity;

        Rigidbody rb = agent.GetComponentInChildren<Rigidbody>();
        if (rb != null)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        AgentControllerBase controller = agent.GetComponentInChildren<AgentControllerBase>(true);
        if (controller == null) return;

        // Facing lives on the controller's own transform (that is what TranslateAgent turns), which
        // is a child of the spawned agent root. Writing the heading onto the root alone composes
        // with whatever local yaw the controller had accumulated, which reads as an offset rather
        // than an absolute heading - so set it in world space on the view transform itself. The
        // yaw-only Euler also drops the pitch and roll TranslateAgent may have left behind.
        Transform view = controller.ViewTransform;
        if (view != null)
            view.rotation = Quaternion.Euler(0f, agentYawDegrees ?? 0f, 0f);

        // Grip and point are toggles, so releasing means toggling only when currently engaged.
        if (controller.IsGripped) controller.ToggleGrip(AgentHandSide.Right);
        if (controller.IsLeftGripped) controller.ToggleGrip(AgentHandSide.Left);
        if (controller.IsPointing) controller.TogglePoint(AgentHandSide.Right);
        if (controller.IsLeftPointing) controller.TogglePoint(AgentHandSide.Left);

        controller.ResetHandPosition(AgentHandSide.Right);
        controller.ResetHandPosition(AgentHandSide.Left);

        if (controller.IsBasketInView) controller.ToggleBasketInView();
    }

    /// <summary>
    /// Waits until freshly spawned shelf items have come to rest, so a screenshot taken right after
    /// the reset shows the same store every time. Bounded: a single item wedged against a collider
    /// must not stall the whole fleet.
    /// </summary>
    private IEnumerator WaitForItemsToSettle()
    {
        const int minimumFixedUpdates = 10;
        const float maximumSettleSeconds = 4f;
        const float pollIntervalSeconds = 0.1f;

        for (int i = 0; i < minimumFixedUpdates; i++)
            yield return new WaitForFixedUpdate();

        float startedAt = Time.realtimeSinceStartup;
        bool stillMoving;
        while ((stillMoving = HasMovingItems()) && Time.realtimeSinceStartup - startedAt < maximumSettleSeconds)
        {
            yield return new WaitForSecondsRealtime(pollIntervalSeconds);
        }

        if (stillMoving)
            Debug.LogWarning($"Items were still moving {maximumSettleSeconds}s after the reset.");

        yield return null;
    }

    private static bool HasMovingItems()
    {
        foreach (GameObject obj in GameObject.FindGameObjectsWithTag("RetailItem"))
        {
            Rigidbody rb = obj.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic && !rb.IsSleeping()) return true;
        }

        return false;
    }
}
