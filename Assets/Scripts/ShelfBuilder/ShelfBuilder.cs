using System.Collections.Generic;
using UnityEngine;


// [ExecuteInEditMode]
public partial class ShelfBuilder : MonoBehaviour
{

    [Header("Shelf Dimensions")]
    [Tooltip("Determines width of the shelf")]
    public float shelfWidth;
    [Tooltip("Determines thickness of the lowest shelf")]
    public float shelfBootHeight;
    [Tooltip("Determines how many layers of shelves there will be")]
    public int shelfLevels;
    [Tooltip("Determines the distance between each layer of shelves")]
    public float distanceBetweenLevels;
    [Tooltip("Determines shelf rotation")]
    [Range(0, 360)]
    public float rotationY;

    [Header("Shelf Configuration")]
    public float shelfRoofHeight;

    public int shelfId;

    public ShelfConfiguration frontShelfConfig;
    public ShelfConfiguration backShelfConfig;
    public ShelfConfiguration leftShelfConfig;
    public ShelfConfiguration rightShelfConfig;

    [Header("Item Spawning")]
    [Tooltip("Determines if items should be spawned at all")]
    public bool spawnItems;
    [Tooltip("Determines if price tags should be spawned")]
    public bool spawnPriceTags;
    [Tooltip("Determines if to spawn randomly picked items from a single category")]
    public ItemSpawnOption itemSpawnOption;
    [Tooltip("Per-sub-shelf item category, keyed by 'subShelfId_subSubShelfId'")]
    public Dictionary<string, ItemCategory> subShelfCategories = new();

    [Header("Debug")]
    [Tooltip("Overrides save data category reads and forces all sub-shelves to use forcedCategory")]
    public bool debugForceItemCategory;
    public ItemCategory forcedCategory;

    [Header("Prefabs/Objects")]
    [Tooltip("The program extrudes this prefab for the shelves")]
    public GameObject shelfSideProfile;
    [Tooltip("Material used for the shelf")]
    public Material shelfMaterial;
    [Tooltip("Material used for the boot (the solid base cube under the lowest shelf)")]
    public Material shelfBootMaterial;
    [Tooltip("Material used for the fridge")]
    public Material metalShelfMaterial;
    [Tooltip("Prefab used for the hinge door. Handle has to be at the door's center.")]
    public GameObject hingeDoorPrefab;
    [Tooltip("Prefab used for the price tag.")]
    public GameObject priceTagPrefab;
    [Tooltip("Cube prefab used for the fridge door borders. Passed down to the hinge doors.")]
    public GameObject borderCube;
    [Tooltip("Prefab for the lit panel above the fridge doors.")]
    public GameObject fridgeDoorLights;
    [Tooltip("Prefab for the fridge badge/logo placed near the lights.")]
    public GameObject fridgeBadge;
    [Tooltip("Vertical rail placed in front of the shelf back wall.")]
    public GameObject shelfRail;
    [Tooltip("Support bracket placed under each shelf, in front of each rail. Its pivot is offset front/back so only its height needs setting.")]
    public GameObject shelfSupport;

    [Tooltip("Material for the shelf walls")]
    public Material wallMaterial;

    // Used in ItemSpawner.cs
    public Material airMaterial;

    [Tooltip("Reference to the floor, so shelves know where to spawn on")]
    public GameObject floor;

    [Header("Fridge Options")]
    [Tooltip("Indicates that this shelf is a fridge.")]
    public bool isFridge;
    [Tooltip("Single door or double door")]
    public FridgeDoorStyle fridgeDoorStyle;

    public float sideShelfWidth;

    // Extra thickness the fridge door border / lights / decor stick out past the glass.
    private const float FridgeBorderThicknessPadding = 0.02f;
    // Fraction of the roof height the lit panel covers (flush against the top).
    private const float DoorLightPercent = 0.8f;
    // Small gap left under the lit panel so it doesn't butt up against the border.
    private const float FrontDecorPadding = 0.01f;
    // Padding between the badge and the right edge of the border cube.
    private const float FridgeBadgeRightPadding = 0.1f;
    // Shelf width above which a third rail is spawned in the middle of the shelf.
    private const float ShelfRailThreshold = 1.5f;
    // Horizontal gap left between the shelf edge and the left/right rails.
    private const float ShelfRailPadding = 0.05f;

    private float subShelfHeight;
    private float subShelfDepth;

    private List<GameObject> shelfObjects;

    private bool _hasBuilt = false;

    public void InitFromSaveData(ShelfSaveData data)
    {
        shelfWidth            = data.shelfWidth;
        shelfBootHeight       = data.shelfBootHeight;
        shelfLevels           = data.shelfLevels;
        distanceBetweenLevels = data.distanceBetweenLevels;
        rotationY             = data.rotationY;
        shelfRoofHeight       = data.shelfRoofHeight;
        frontShelfConfig      = data.frontShelfConfig;
        backShelfConfig       = data.backShelfConfig;
        leftShelfConfig       = data.leftShelfConfig;
        rightShelfConfig      = data.rightShelfConfig;
        spawnItems            = data.spawnItems;
        spawnPriceTags        = data.spawnPriceTags;
        itemSpawnOption       = data.itemSpawnOption;
        subShelfCategories    = data.subShelfCategories ?? new Dictionary<string, ItemCategory>();
        isFridge       = data.spawnHingeDoors;
        fridgeDoorStyle       = data.fridgeDoorStyle;
        shelfId               = data.shelfId;
    }

    void Start()
    {
        if (_hasBuilt) return;

        Build();
    }

    public void Rebuild()
    {
        transform.rotation = Quaternion.identity;

        Build();
    }

    private void Build()
    {
        shelfObjects = new List<GameObject>();

        subShelfHeight = shelfSideProfile.transform.localScale.y;
        subShelfDepth  = shelfSideProfile.transform.localScale.z;

        DestroyAllChildren();
        BuildRectangularShelf();
        SpawnItemsOnAllShelves();
        _hasBuilt = true;
    }

    void DestroyAllChildren()
    {
        while (transform.childCount > 0)
        {
            Transform child = transform.GetChild(0);
            child.SetParent(null);

            if (Application.isPlaying)
                Destroy(child.gameObject);
            else
                DestroyImmediate(child.gameObject);
        }
    }
}
