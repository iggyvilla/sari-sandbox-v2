using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

public class ItemSpawner : MonoBehaviour
{
    private float widthBudget;
    private float depthBudget;
    private float shelfWidth;
    // Set by ShelfBuilder, specified by user
    private float heightBudget;
    
    private ShelfBuilder shelfBuilder; // get from parent
    private ItemCategories itemCategories;
    public float itemOuterPadding;
    public float itemBackPadding;
    public float interItemPadding;
    public float fillFraction;
    private ItemCategoryType itemCategory;

    private float direction;
    private List<GameObject> triggers;
    private LayerMask itemTriggerMask;
    
    public ShelfItemData shelfItemData;

    private bool itemsSpawnRandomly = true;
    
    void Awake()
    {
        triggers = new List<GameObject>();
        
        // Instantiate a DataHandler to access objects
        while (itemCategories == null)
        {
            itemCategories = DataHandler.Instance.itemCategories;
        }
        
        itemTriggerMask = LayerMask.NameToLayer("GroceryItemTrigger");
        
        shelfItemData = GetComponent<ShelfItemData>();
        
        UpdateShelfDimensions();
        
        /*
         * We don't spawn products immediately here 
         * because we process scaling/rotation first
         * in ShelfBuilder.cs, then we use SpawnProducts()
         */
    }

    public void Init(float distanceBetweenShelves, bool spawnRandomly, ItemCategoryType category)
    {
        heightBudget = distanceBetweenShelves;
        itemsSpawnRandomly = spawnRandomly;
        itemCategory = category;
    }

    void UpdateShelfDimensions()
    {
        // Not sure if there's a better name for this
        direction = CalculateDirectionInteger();
        
        Renderer r = GetComponent<Renderer>();
        
        if (ShelfIsFacingZ()) {
            widthBudget = r.bounds.size.x;
            depthBudget = r.bounds.size.z;
        }
        else
        {
            widthBudget = r.bounds.size.z;
            depthBudget = r.bounds.size.x;   
        }
        
        // How "thick" the shelf is
        shelfWidth = r.bounds.size.y;
    }

    bool ShelfIsFacingZ()
    {
        float dp = Vector3.Dot(transform.forward, Vector3.forward);
        if (Math.Abs((int)dp) == 1) return true;
        return false;
    }

    int CalculateDirectionInteger()
    {
        Vector3 forward = transform.forward;

        if (forward == Vector3.right || forward == Vector3.forward) return 1;
        return -1;
    }

    public void SpawnProducts()
    {
        // Update our knowledge of the shelf dimensions
        UpdateShelfDimensions();
        
        if (itemsSpawnRandomly) shelfItemData.RandomFillWithCategory(itemCategory, interItemPadding, widthBudget);
        
        // Tracks how far along the shelf we are
        // float lengthwiseOffset = 0.0f;
        
        float lengthwiseOffset = Math.Max(0, (widthBudget - shelfItemData.itemsTotalWidth)/2);
        
        Debug.Log(lengthwiseOffset);
        
        bool firstItem = true;
        
        foreach (var shelfItem in shelfItemData.shelfItems)
        {
            GameObject product = shelfItem.prefab;
            
            float itemDepth = shelfItem.dimensions.depth;
            float itemWidth = shelfItem.dimensions.width;
            float itemHeight = shelfItem.dimensions.height;
            
            /*
             * only worry about interItemPadding if it 
             * isn't the leftmost/first item on the shelf
             */
            lengthwiseOffset += itemWidth/2 + (!firstItem ? interItemPadding : 0);
            
            /* stop spawning if the item we're about to spawn is outside the shelf */
            if (lengthwiseOffset + itemWidth/2 + itemOuterPadding > widthBudget) break;

            int numRows = CalculateRows(itemDepth);
            int numStack = CalculateStackHeight(itemHeight, itemCategory);
            
            for (int j = 0; j < numRows; j++)
            {
                for (int k = 0; k < numStack; k++)
                {
                    Vector3 spawnPosition =
                        GenerateSpawnPositionOnShelf(
                            lengthwiseOffset,
                            itemDepth, 
                            itemHeight, 
                            j, 
                            k
                        );

                    DrawData drawData = 
                        GenerateProductDrawData(product, spawnPosition);
                        
                    GPUInstanceTracker.Instance.AddToInstance(
                        product.name,
                        product,
                        itemHeight,
                        drawData
                    );
                }
            }
            
            GenerateBoundingBoxTriggerForItem(
                lengthwiseOffset, 
                itemHeight, 
                itemWidth, 
                numStack, 
                product.name
            );

            firstItem = false;
            lengthwiseOffset += itemWidth/2;
        }
    }

    private void OnDestroy()
    {
        foreach (var trigger in triggers)
        {
            Destroy(trigger);
        }
    }

    int CalculateRows(float itemWidth)
    {
        return (int) ((depthBudget-itemOuterPadding-itemBackPadding) /
                      (itemWidth + interItemPadding));
    }

    void GenerateBoundingBoxTriggerForItem(float lengthwiseOffset, float itemHeight, float itemWidth, float numStack, string productName)
    {
        /* Setup box collider trigger for item retrieval */
        GameObject itemTrigger = new GameObject();
        BoxCollider b = itemTrigger.AddComponent<BoxCollider>();
        
        /* I'm not sure why this works, but it does */
        itemTrigger.transform.position = GenerateSpawnPositionOnShelf(
            lengthwiseOffset,
            0,
            (itemHeight * numStack)/2,
            1,
            1,
            true
        );
        itemTrigger.layer = itemTriggerMask;
            
        b.name = productName;
        b.isTrigger = true;
            
        b.size = new Vector3(
            ShelfIsFacingZ() ? itemWidth : depthBudget,
            itemHeight * numStack,
            ShelfIsFacingZ() ? depthBudget : itemWidth
        );
            
        itemTrigger.transform.SetParent(transform);
    }

    DrawData GenerateProductDrawData(GameObject product, Vector3 spawnPosition)
    {
        /*
         * get the transforms of LOD0 (or LOD1)
         * if you get the transforms of the product itself,
         * some items won't spawn properly (local vs world coords)
         */
        Transform prodChild = product.transform.GetChild(0);
                    
        Transform lodTransform = null;
        foreach (Transform child in prodChild)
        {
            if (child.name.EndsWith("_LOD0"))
            {
                lodTransform = child;
                break;
            }
        }
                    
        if (lodTransform is null)
        {
            Debug.LogWarning("Could not find LOD0 for object " + product.name);
            lodTransform = prodChild;
        }
                    
        /* prepare DrawData used later in custom URP shader */
        Quaternion q =
            Quaternion.Euler(0, DegreesToAisle(), 0) *
            lodTransform.transform.rotation;
                        
        DrawData drawData = new DrawData
        {
            position = spawnPosition,
            rotation = new Vector4(q.x, q.y, q.z, q.w),
            scale = lodTransform.transform.lossyScale
        };
        
        return drawData;
    }
    
    Vector3 GenerateSpawnPositionOnShelf(float lengthwiseOffset, float itemDepth, float itemHeight, int rowNum, int stackNum, bool bBoxDepth = false)
    {
        Vector3 shelfPos = transform.position;
        
        float sideOffset =
            (widthBudget/2 - itemOuterPadding - lengthwiseOffset) *
            direction;

        float backOffset;
        if (bBoxDepth)
        {
            backOffset = itemDepth * direction;
        }
        else
        {
            backOffset =
                (depthBudget/2 - ((itemDepth + interItemPadding) 
                                  * (rowNum + 0.5f)) - itemOuterPadding) * direction;
        }
        
        /* A shelf's side and back differs depending on how its rotated */
        Vector3 spawnPosition = new Vector3(
            // stacks items sidewards
            shelfPos.x + (ShelfIsFacingZ() ? sideOffset : backOffset),
            // stacks items (only stackable ones) upwards
            shelfPos.y + shelfWidth / 2 + itemHeight * stackNum,
            // stacks items backwards
            shelfPos.z + (ShelfIsFacingZ() ? backOffset : sideOffset)
        );

        return spawnPosition;
    }

    // i feel like there's a better way to do this...
    float DegreesToAisle()
    {
        Vector3 fwd = transform.forward;

        if (fwd == Vector3.left) return 0;
        if (fwd == Vector3.forward) return 90;
        if (fwd == Vector3.right) return 180;
        if (fwd == Vector3.back) return 270;
        return 0;
    }

    GameObject GetRandomProduct(ItemCategoryType itemCategory)
    {
        string[] categoryIds = itemCategories.Categories[(int)itemCategory].Items;
        string chosenId = categoryIds[Random.Range(0, categoryIds.Length)];
        
        // return Resources.Load<GameObject>("Prefabs/Products/LIBBYS_VIENNA_SAUSAGE_130G");
        // return Resources.Load<GameObject>("Prefabs/Products/NESTLE_KOKOKRUNCH_CHOCOLATE_330G");
        
        return Resources.Load<GameObject>("Prefabs/Products/" + chosenId);
    }

    int CalculateStackHeight(float itemHeight, ItemCategoryType category)
    {
        // TODO: can implement randomness for row front (i.e., iteration = 0)
        
        // stack only if of type "Can" or "Biscuit"
        if (category is ItemCategoryType.Can)
        {
            return (int)((heightBudget * fillFraction) / itemHeight);
        }
        
        return 1;
    }
}
