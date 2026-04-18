using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;


// [ExecuteInEditMode]
public class ShelfBuilder : MonoBehaviour
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
    [Tooltip("If items spawn randomly, determines what category of items to spawn")]
    public ItemCategory itemCategory;
    
    [Header("Prefabs/Objects")]
    [Tooltip("The program extrudes this prefab for the shelves")]
    public GameObject shelfSideProfile;
    [Tooltip("Material used for the shelf")]
    public Material shelfMaterial;
    [Tooltip("Material used for the fridge")]
    public Material metalShelfMaterial;
    [Tooltip("Prefab used for the hinge door. Handle has to be at the door's center.")]
    public GameObject hingeDoorPrefab;
    [Tooltip("Prefab used for the price tag.")]
    public GameObject priceTagPrefab;
    
    [Tooltip("Material for the shelf walls")]
    public Material wallMaterial;
    
    // Used in ItemSpawner.cs
    public Material airMaterial;
    
    [Tooltip("Reference to the floor, so shelves know where to spawn on")]
    public GameObject floor;
    // [Tooltip("DEBUG ONLY: Spawns template shelves in editor mode")]
    // public bool spawnShelves;
    
    [Header("Fridge Options")]
    [Tooltip("Spawn fridge hinge doors. Automatically switches shelf material to metal.")]
    public bool spawnHingeDoors;
    [Tooltip("Single door or double door")]
    public FridgeDoorStyle fridgeDoorStyle;

    public float sideShelfWidth;
    
    private float subShelfHeight;
    private float subShelfDepth;
    
    private List<GameObject> shelfObjects;

    private bool _itemsAlreadySpawned = false;
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
        itemCategory          = data.itemCategory;
        spawnHingeDoors       = data.spawnHingeDoors;
        fridgeDoorStyle       = data.fridgeDoorStyle;
        shelfId               = data.shelfId;
    }

    void Start()
    {
        if (_hasBuilt) return;

        shelfObjects = new List<GameObject>();

        subShelfHeight = shelfSideProfile.transform.localScale.y;
        subShelfDepth  = shelfSideProfile.transform.localScale.z;

        DestroyAllChildren();
        BuildRectangularShelf();
        SpawnItemsOnAllShelves();
    }

    public void Rebuild()
    {
        shelfObjects       = new List<GameObject>();
        transform.rotation = Quaternion.identity;

        subShelfHeight = shelfSideProfile.transform.localScale.y;
        subShelfDepth  = shelfSideProfile.transform.localScale.z;

        DestroyAllChildren();
        BuildRectangularShelf();
        _hasBuilt = true;
    }

    public void DespawnShelfItems()
    {
        foreach (ItemBBoxInfo bboxInfo in GetComponentsInChildren<ItemBBoxInfo>())
        {
            bboxInfo.DeleteAllItems();
            Destroy(bboxInfo.gameObject);
        }
    }
    
    public static void DeleteAllPriceTags()
    {
        PriceTag[] instances =
            FindObjectsByType<PriceTag>(FindObjectsSortMode.None);

        foreach (PriceTag instance in instances)
        {
            // Destroy the GameObject the script is attached to
            Destroy(instance.gameObject);
        }
    }
    
    public static void DespawnAllItemsInScene()
    {
        GPUInstanceTracker.Instance.DespawnAllItems();
        DeleteAllPriceTags();
    }
    
    void BuildRectangularShelf()
    {
        float wallThickness = subShelfHeight;
        float groundY = floor.transform.position.y;

        if (spawnHingeDoors)
        {
            shelfMaterial = metalShelfMaterial;
            wallMaterial = metalShelfMaterial;
        }
        
        // Build the lengthwise shelves
        float shelvesZOffset = (subShelfDepth + wallThickness) / 2;
        BuildSubShelf(
            transform, 
            MyZWithOffset(shelvesZOffset),
            0,
            groundY, 
            0, 
            shelfWidth,
            frontShelfConfig
        );
        
        BuildSubShelf(
            transform, 
            MyZWithOffset(-shelvesZOffset),
            1,
            groundY, 
            180, 
            shelfWidth,
            backShelfConfig
        );
        
        // Build width-wise shelves
        float shelvesXOffset = subShelfDepth / 2 + shelfWidth / 2 + wallThickness;

        sideShelfWidth = CalculateShelfWidth(
            wallThickness, 
            frontShelfConfig, 
            backShelfConfig
        );
        
        BuildSubShelf(
            transform, 
            MyPosWithOffset(shelvesXOffset, sideShelfWidth, rotationY, frontShelfConfig, backShelfConfig),
            2,
            groundY, 
            90,
            sideShelfWidth,
            leftShelfConfig
        );
        
        BuildSubShelf(
            transform, 
            MyPosWithOffset(-shelvesXOffset, sideShelfWidth, rotationY, frontShelfConfig, backShelfConfig),
            3,
            groundY, 
            270,
            sideShelfWidth,
            rightShelfConfig
        );
        
        // Must do all rotations/translations first before spawning items
        transform.Rotate(Vector3.up, rotationY);
        
        SpawnHingeDoors();
    }

    void SpawnHingeDoors()
    {
        if (!spawnHingeDoors) return;

        float doorDepth = 0.02f;
        float doorHeight = (shelfLevels * distanceBetweenLevels);
        
        bool isDoubleDoor = fridgeDoorStyle == FridgeDoorStyle.Double;
        
        Vector3 leftDoorPos = new Vector3(
            transform.position.x,
            doorHeight/2 + shelfBootHeight,
            transform.position.z
        ) 
            + transform.forward * (subShelfDepth + 0.03f) 
            + transform.right * (isDoubleDoor ? shelfWidth/4 : 0); 
        
        GameObject leftDoor = Instantiate(hingeDoorPrefab, leftDoorPos, transform.rotation, transform);
        RemovePhysicsIfInStoreBuilder(leftDoor);
        
        HingedDoorBuilder leftDoorBuilder = leftDoor.GetComponentInChildren<HingedDoorBuilder>();
        Vector3 lDoorDims = new Vector3(
            shelfWidth/(isDoubleDoor ? 2 : 1),
            doorHeight,
            doorDepth
        );
        leftDoorBuilder.BuildHingeDoor(lDoorDims, 0.05f, DoorDirection.Left);

        if (!isDoubleDoor) return;
        // Right door
        
        Vector3 rDoorPos = new Vector3(
            transform.position.x,
            doorHeight/2 + shelfBootHeight,
            transform.position.z
        ) 
            + transform.forward * (subShelfDepth + 0.03f)
            - transform.right * shelfWidth/4; 
        
        GameObject rDoor = Instantiate(hingeDoorPrefab, rDoorPos, transform.rotation, transform);
        RemovePhysicsIfInStoreBuilder(rDoor);
        
        HingedDoorBuilder rDoorBuilder = rDoor.GetComponentInChildren<HingedDoorBuilder>();
        Vector3 rDoorDims = new Vector3(
            shelfWidth/2,
            doorHeight,
            doorDepth
        );
        rDoorBuilder.BuildHingeDoor(rDoorDims, 0.05f, DoorDirection.Right);
        
    }

    void RemovePhysicsIfInStoreBuilder(GameObject hingeDoor)
    {
        string sceneName = SceneManager.GetActiveScene().name;
        if (sceneName != "StoreBuilder") return;
        Destroy(hingeDoor.GetComponent<HingeJoint>());
        Destroy(hingeDoor.GetComponent<Rigidbody>());
    }

    float CalculateShelfWidth(float wallThickness, ShelfConfiguration frontShelfCfg, ShelfConfiguration backShelfCfg)
    {
        int divisor = 1;

        // If we should build only one shelf, half the width
        if (frontShelfCfg.buildShelves ^ backShelfCfg.buildShelves) divisor++;
        
        return (subShelfDepth*2 + wallThickness) / divisor;
    }

    public float CalculateShelfHeight()
    {
        return (distanceBetweenLevels * shelfLevels) 
               + shelfBootHeight
               + shelfRoofHeight;
    }
    
    /* 
     *  2D CROSS-SECTION (NOT TOP VIEW):
     *          |----------------------------|
     *     ^    |                            |
     *     |    |----------------------------|
     *  Height           Depth -->
     *
     *  Width is how much it extrudes.
     */
    void BuildSubShelf(Transform parent, Vector3 spawnPos, int subShelfId, float floorY, float rotY, float width, ShelfConfiguration shelfConfig)
    {
        
        GameObject emptyParent = new GameObject("ShelfGroup" + shelfId);
        emptyParent.transform.position = spawnPos;
        emptyParent.transform.SetParent(parent);
        
        if (shelfConfig.buildBackWall) BuildShelfWall(subShelfHeight, width, subShelfDepth, emptyParent.transform);
        if (!shelfConfig.buildShelves)
        {
            emptyParent.transform.Rotate(Vector3.up, rotY);
            return;
        }
        
        // Build shelves with the ShelfBuilder empty as the center (x and z-wise) 
        // for the y coordinate, use the y coord of the floor (floorY)
        Vector3 shelfPosition = new Vector3(
            spawnPos.x, 
            floorY + shelfBootHeight / 2, 
            spawnPos.z
        );

        // Instantiate 1 shelf for each level, starting from the bottom
        for (int i = 0; i < shelfLevels + 1; i++)
        {
            if (!shelfConfig.buildShelfRoof && i == shelfLevels) continue;
            
            bool isBottomShelf = i == 0;
            bool roof = i == shelfLevels && shelfConfig.buildShelfRoof;
            
            if (roof) shelfPosition.y += shelfRoofHeight / 2;
            
            // Instantiate shelf
            GameObject shelfExtruded = Instantiate(
                shelfSideProfile, 
                shelfPosition, 
                shelfSideProfile.transform.rotation, 
                parent
            );
            
            // Mark as an occludee for ray-casting later on
            shelfExtruded.layer = LayerMask.NameToLayer("SariInteractable");
            shelfExtruded.tag = "Wall";
            
            shelfExtruded.GetComponent<Renderer>().material = shelfMaterial;
            
            shelfExtruded.name = "Shelf" + i;
            
            // Extrude the shelf to the desired width via scaling
            Vector3 extrudedScale = shelfSideProfile.transform.localScale;
            extrudedScale.x = width;

            if (isBottomShelf || roof)
                extrudedScale.y = roof ? shelfRoofHeight : shelfBootHeight;
            
            shelfExtruded.transform.localScale = extrudedScale;
            
            if (spawnItems && !roof)
            {
                ItemSpawner spawner = shelfExtruded.GetComponent<ItemSpawner>();
                ShelfInfo sInfo = new ShelfInfo
                {
                    shelfId = shelfId,
                    subShelfId = subShelfId,
                    subSubShelfId = i,
                };
                
                
                spawner.Init(
                    distanceBetweenLevels,
                    itemSpawnOption,
                    spawnPriceTags,
                    itemCategory,
                    airMaterial,
                    priceTagPrefab,
                    sInfo
                );
                
                shelfObjects.Add(shelfExtruded);
            }
            
            // set as static for performance
            shelfExtruded.isStatic = true;
            
            // prepare coords for shelf at the next level
            shelfPosition.y += distanceBetweenLevels + (isBottomShelf ? shelfBootHeight/2 : roof ? shelfRoofHeight/2 : 0);
            
            // set parent to the empty
            shelfExtruded.transform.SetParent(emptyParent.transform);
        }
        
        emptyParent.transform.Rotate(Vector3.up, rotY);
    }

    public void SpawnItemsOnAllShelves()
    {
        if (spawnItems)
        {
            foreach (var shelf in shelfObjects)
            {
                ItemSpawner spawner = shelf.GetComponent<ItemSpawner>();
                spawner.SpawnProducts();
            }
        }

        _itemsAlreadySpawned = true;
    }
    
    
    public void SummonOutlineBox(SB_UIHandler uiHandler, SB_InteractionController interactionController)
    {
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.layer = LayerMask.NameToLayer("SariInteractable");
        cube.GetComponent<Renderer>().material = airMaterial;

        // Make collider a trigger so it doesn't interfere with physics
        // but is still hit by raycasts (Physics.queriesHitTriggers = true by default)
        cube.GetComponent<BoxCollider>().isTrigger = true;
        
        // Add OutlineFx for the white outline
        cube.AddComponent<OutlineFx.OutlineFx>();

        // Wire up the selector
        ShelfSelector selector = cube.AddComponent<ShelfSelector>();
        selector.assignedShelf = this;
        selector.uiHandler = uiHandler;
        selector.interactionController = interactionController;
        
        // Encapsulate shelf bounds
        selector.EncapsulateShelf(this);
    }
    
    // wallOffset is how far from the edge of a shelf the wall will spawn at
    void BuildShelfWall(float wallThickness, float wallWidth, float wallOffset, Transform parent)
    {
        // float wallHeight = distanceBetweenLevels * shelfLevels + shelfBootHeight + shelfRoofHeight;
        float wallHeight = CalculateShelfHeight();
        
        GameObject backWall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        backWall.layer = LayerMask.NameToLayer("SariInteractable");
        backWall.tag = "Wall";
        backWall.name = "BackWall";
        backWall.transform.localScale = new Vector3(
            wallWidth,
            wallHeight,
            wallThickness
        );
        
        Vector3 backWallPos = parent.position;
        backWallPos.z = parent.position.z - (wallOffset + wallThickness) / 2;
        backWallPos.y = wallHeight / 2;
        backWall.transform.position = backWallPos;
        
        // set as static for performance
        backWall.isStatic = true;
        
        // Assign wall material
        Renderer wallRenderer = backWall.GetComponent<Renderer>();
        wallRenderer.material = wallMaterial;
        // Assign the parent 
        backWall.transform.SetParent(parent);
    }

    Vector3 MyPosWithOffset(float offset, float sideShelfWidth, float rotY, ShelfConfiguration frontShelfCfg, ShelfConfiguration backShelfCfg)
    {
        float zOffset = 0;
        
        if (frontShelfCfg.buildShelves ^ backShelfCfg.buildShelves)
        {
            float zOffTemp = sideShelfWidth / 2;
            zOffset = frontShelfCfg.buildShelves ? zOffTemp : -zOffTemp;
        }
        
        return new Vector3(
            transform.position.x + offset, 
            transform.position.y, 
            transform.position.z + zOffset
        );   
    }
    
    Vector3 MyZWithOffset(float offset)
    {
        return new Vector3(
            transform.position.x, 
            transform.position.y, 
            transform.position.z + offset
        );   
    }

    void DestroyAllChildren()
    {
        while (transform.childCount > 0)
        {
            DestroyImmediate(transform.GetChild(0).gameObject);
        }
    }
    
    void Update()
    {
        // #if UNITY_EDITOR
        // // Only used in the editor
        // if (spawnShelves)
        // {
        //     bool prevSpawnItems = spawnItems;
        //     DestroyAllChildren();
        //     spawnShelves = false;
        //     spawnItems = false;
        //     // BuildRectangularShelf();
        //     spawnItems = prevSpawnItems;
        // }
        // #endif
    }
}
