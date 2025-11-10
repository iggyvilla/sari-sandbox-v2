using System;
using UnityEngine;
using Random = UnityEngine.Random;

public class ItemSpawner : MonoBehaviour
{

    public bool spawnItems;
    
    private float widthBudget;
    private float depthBudget;
    public float heightBudget;
    private float shelfWidth;
    
    private ShelfBuilder shelfBuilder; // get from parent
    private ItemCategories itemCategories;
    public float itemOuterPadding;
    public float itemBackPadding;
    public float interItemPadding;
    public float fillFraction;
    public int categoryIndex;

    private float direction;
    
    void Awake()
    {
        itemCategories = FindFirstObjectByType<DataHandler>().itemCategories;
    }
    
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
        Debug.Log(SystemInfo.maxComputeWorkGroupSize);
        
        Renderer r = GetComponent<Renderer>();
        
        // not sure if theres a better name for this
        direction = CalculateDirectionInteger();
        
        if (ShelfIsFacingZ()) {
            widthBudget = r.bounds.size.x;
            depthBudget = r.bounds.size.z;
        }
        else
        {
            widthBudget = r.bounds.size.z;
            depthBudget = r.bounds.size.x;   
        }
        
        // how thick the shelf is
        shelfWidth = r.bounds.size.y;
        
        // TODO: change to shelfBuilder.distanceBetweenLevels
        heightBudget = 0.41f;
        
        // Sometimes you'd want to spawn 
        // an empty shelf for debugging
        if (spawnItems) SpawnProducts();
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

    void SpawnProducts()
    {
        float lengthwiseOffset = 0.0f;
        bool firstItem = true;
        
        while (lengthwiseOffset < widthBudget)
        {

            GameObject product = GetRandomProduct();
            
            /*
             * get product dimensions
             * for some reason, the bounds are bigger than the item 
             * in some items. just something to keep in mind.
             */ 
            MeshRenderer r = product.GetComponentInChildren<MeshRenderer>();
            float itemLength = r.bounds.size.x;
            float itemWidth = r.bounds.size.z;
            float itemHeight = r.bounds.size.y;
            
            // only worry about interItemPadding if it isn't the 
            // leftmost/first item on the shelf
            lengthwiseOffset += itemWidth/2 + (!firstItem ? interItemPadding : 0);
            
            // stop spawning if the item we're about to spawn is outside the shelf
            if (lengthwiseOffset + itemWidth/2 + itemOuterPadding > widthBudget) break;

            // physics will be disabled until the player is near
            Rigidbody rb = product.GetComponentInChildren<Rigidbody>();
            DisablePhysics(rb);
            
            for (int j = 0; j < CalculateRows(itemWidth); j++)
            {
                // Only stack if of type Can, otherwise it just spawns one
                for (int k = 0; k < CalculateStackHeight(itemHeight, "Can"); k++)
                {
                    
                    Vector3 spawnPosition =
                        GenerateSpawnPositionOnShelf(
                            lengthwiseOffset,
                            itemLength, 
                            itemHeight, 
                            j, 
                            k
                        );
                    
                    /*
                     * get the transforms of LOD0 (or LOD1)
                     * if you get the transforms of the product itself,
                     * some items won't spawn properly (local vs world coords)
                     */
                    Transform prodChild = product.transform.GetChild(0);
                    Transform lodTransform = prodChild.Find(prodChild.name + "_LOD0");

                    if (lodTransform != null)
                    {
                        GPUInstanceTracker.Instance.AddToInstance(
                            product.name,
                            product,
                            Matrix4x4.TRS(
                                spawnPosition,
                                Quaternion.Euler(0, DegreesToAisle(), 0) * lodTransform.transform.rotation,
                                lodTransform.transform.lossyScale
                            )  
                        );
                    }
                    else
                    {
                        Debug.LogError("Could not find LOD0 for object " + product.name);
                    }
                    

                    // spawnedItem.isStatic = true;
                    
                    // makes the object face the aisle
                    // spawnedItem.transform.Rotate(
                    //     Vector3.up, 
                    //     DegreesToAisle()
                    // );

                    // TODO: parent under an empty for organization
                }
            }

            firstItem = false;
            lengthwiseOffset += itemWidth/2;
        }
    }

    int CalculateRows(float itemWidth)
    {
        return (int)((depthBudget-itemOuterPadding-itemBackPadding) / itemWidth);
    }

    Vector3 GenerateSpawnPositionOnShelf(float lengthwiseOffset, float itemLength, float itemHeight, int rowNum, int stackNum)
    {
        Vector3 shelfPos = transform.position;
        
        float sideOffset =
            (widthBudget / 2 - itemOuterPadding - lengthwiseOffset) *
            direction;
        float backOffset =
            (depthBudget / 2 - ((itemLength + interItemPadding) *
                                (rowNum + 0.5f)) -
             itemOuterPadding) * direction;
        
        Vector3 spawnPosition = new Vector3(
            // stacks items sidewards
            shelfPos.x + (ShelfIsFacingZ() ? sideOffset : backOffset),
            // stacks items (only cans) upwards
            shelfPos.y + shelfWidth / 2 + itemHeight * stackNum,
            // stacks items backwards
            shelfPos.z + (ShelfIsFacingZ() ? backOffset : sideOffset)
        );

        return spawnPosition;
    }

    // theres definitely a better way to do this
    float DegreesToAisle()
    {
        Vector3 fwd = transform.forward;

        if (fwd == Vector3.left) return 0;
        if (fwd == Vector3.forward) return 90;
        if (fwd == Vector3.right) return 180;
        if (fwd == Vector3.back) return 270;
        return 0;
    }

    GameObject GetRandomProduct()
    {
        string[] canIds = itemCategories.Categories[6].Items;
        string chosenId = canIds[Random.Range(0, canIds.Length)];
        return Resources.Load<GameObject>("Prefabs/Products/" + chosenId);
        //"PUREFOODS_CHINESE_STYLE_LUNCHEON_MEAT_350G"
    }

    int CalculateStackHeight(float itemHeight, string category)
    {
        // can implement randomness for row front (i.e., iteration = 0)
        
        // stack only if of type "Can"
        if (category == "Can")
        {
            return (int)((heightBudget * fillFraction) / itemHeight);
        }
        
        return 1;
    }

    void DisablePhysics(Rigidbody rb)
    {
        rb.isKinematic = true;
        rb.linearVelocity = Vector3.zero;
        rb.useGravity = false;
        rb.interpolation = RigidbodyInterpolation.None;
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
