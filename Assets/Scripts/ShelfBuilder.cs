using System.Collections.Generic;
using UnityEngine;

[ExecuteInEditMode]
public class ShelfBuilder : MonoBehaviour
{
    
    [Header("Shelf Dimensions")]
    public float shelvesLength;
    [Tooltip("Determines thickness of the lowest shelf")]
    public float shelfBootHeight;
    [Tooltip("Determines how many layers of shelves there will be")]
    public int shelfLevels;
    [Tooltip("Determines the distance between each layer of shelves")]
    [SerializeField] private float distanceBetweenLevels;
    [Tooltip("Determines shelf rotation")]
    [Range(0, 360)]
    public float rotationY;
    
    [Header("Item Spawning")]
    [Tooltip("Determines if items should be spawned at all")]
    public bool spawnItems;
    [Tooltip("Determines if to spawn randomly picked items from a single category")]
    public bool itemsSpawnRandomly;
    [Tooltip("If items spawn randomly, determines what category of items to spawn")]
    public ItemCategoryType itemCategory;
    
    [Header("Prefabs/Objects")]
    [Tooltip("The program extrudes this prefab for the shelves")]
    public GameObject shelfSideProfile;
    [Tooltip("Material for the shelf walls")]
    public Material wallMaterial;
    [Tooltip("Reference to the floor, so shelves know where to spawn on")]
    public GameObject floor;
    [Tooltip("Determines if shelves should be spawned at all")]
    public bool spawnShelves;
    
    private float shelfHeight;
    private float shelfLength;

    
    private List<GameObject> shelfObjects;
    
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        shelfObjects = new List<GameObject>();
        
        shelfHeight = shelfSideProfile.transform.localScale.y;
        shelfLength = shelfSideProfile.transform.localScale.z;
        
        DestroyAllChildren();
        
        BuildRectangularShelf();
    }
    
    void BuildRectangularShelf()
    {
        float wallThickness = shelfHeight;
        float groundY = floor.transform.position.y;
        
        // Build the lengthwise shelves
        float shelvesZOffset = (shelfLength + wallThickness) / 2;
        BuildShelves(
            transform, 
            myZWithOffset(shelvesZOffset),
            groundY, 
            0, 
            shelvesLength,
            true
        );
        BuildShelves(
            transform, 
            myZWithOffset(-shelvesZOffset),
            groundY, 
            180, 
            shelvesLength,
            false
        );
        
        // Build width-wise shelves
        float shelvesXOffset = shelfLength / 2 + shelvesLength / 2 + wallThickness;
        BuildShelves(
            transform, 
            myXWithOffset(shelvesXOffset),
            groundY, 
            90,
            (shelfLength * 2) + wallThickness,
            true
        );
        BuildShelves(
            transform, 
            myXWithOffset(-shelvesXOffset),
            groundY, 
            270,
            (shelfLength * 2) + wallThickness,
            true
        );
        
        // Must do all rotations/translations first before spawning items
        transform.Rotate(Vector3.up, rotationY);

        SpawnItemsOnAllShelves();
    }
    
    /* 
     *  2D CROSS-SECTION (NOT TOP VIEW):
     *          |----------------------------|
     *     ^    |                            |
     *     |    |----------------------------|
     *  Height           Length -->
     *
     *  Width is how much it extrudes.
     */
    void BuildShelves(Transform parent, Vector3 spawnPos, float floorY, float rotY, float width, bool buildWall)
    {
        
        GameObject emptyParent = new GameObject("SideShelves");
        emptyParent.transform.position = spawnPos;
        emptyParent.transform.SetParent(parent);
        
        if (buildWall) BuildShelfWall(shelfHeight, width, shelfLength, emptyParent.transform);
        
        // Build shelves with the ShelfBuilder empty as the center (x and z-wise) 
        // for the y coordinate, use the y coord of the floor (floorY)
        Vector3 shelfPosition = new Vector3(
            spawnPos.x, 
            floorY + shelfBootHeight / 2, 
            spawnPos.z
        );

        // summon 1 shelf for each level, starting from the bottom
        for (int i = 0; i < shelfLevels; i++)
        {
            bool isBottomShelf = i == 0;
            
            // instantiate shelf
            GameObject shelfExtruded = Instantiate(
                shelfSideProfile, 
                shelfPosition, 
                shelfSideProfile.transform.rotation, 
                parent
            );
            
            shelfExtruded.name = "Shelf" + i;
            
            // extrude the shelf to the desired width via scaling
            Vector3 extrudedScale = shelfSideProfile.transform.localScale;
            extrudedScale.x = width;
            if (isBottomShelf) extrudedScale.y = shelfBootHeight;
            
            shelfExtruded.transform.localScale = extrudedScale;
            
            
            if (spawnItems)
            {
                ItemSpawner spawner = shelfExtruded.GetComponent<ItemSpawner>();
                
                spawner.Init(
                    distanceBetweenLevels,
                    itemsSpawnRandomly,
                    itemCategory
                );
                
                shelfObjects.Add(shelfExtruded);
            }
            
            // set as static for performance
            shelfExtruded.isStatic = true;
            
            // prepare coords for shelf at the next level
            shelfPosition.y += distanceBetweenLevels + (isBottomShelf ? shelfBootHeight/2 : 0);
            
            // set parent to the empty
            shelfExtruded.transform.SetParent(emptyParent.transform);
        }
        
        emptyParent.transform.Rotate(Vector3.up, rotY);
    }

    void SpawnItemsOnAllShelves()
    {
        if (spawnItems)
        {
            foreach (var shelf in shelfObjects)
            {
                ItemSpawner spawner = shelf.GetComponent<ItemSpawner>();
                spawner.SpawnProducts();
            }
        }
    }
    
    // wallOffset is how far from the edge of a shelf the wall will spawn at
    void BuildShelfWall(float wallThickness, float wallWidth, float wallOffset, Transform parent)
    {
        float wallHeight = distanceBetweenLevels * shelfLevels + shelfBootHeight;
        
        GameObject backWall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        backWall.layer = LayerMask.NameToLayer("Wall");
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

    Vector3 myXWithOffset(float offset)
    {
        return new Vector3(
            transform.position.x + offset, 
            transform.position.y, 
            transform.position.z
        );   
    }
    
    Vector3 myZWithOffset(float offset)
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
        #if UNITY_EDITOR
        // Only used in the editor
        if (spawnShelves)
        {
            bool prevSpawnItems = spawnItems;
            DestroyAllChildren();
            spawnShelves = false;
            spawnItems = false;
            // BuildRectangularShelf();
            spawnItems = prevSpawnItems;
        }
        #endif
    }
}
