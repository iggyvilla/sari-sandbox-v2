using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using Unity.VisualScripting;
using UnityEngine;
using Random = UnityEngine.Random;

public class ItemSpawner : MonoBehaviour
{
    private const float StackVerticalClearance = 0.001f;

    private float widthBudget;
    private float depthBudget;
    private float shelfWidth;
    // Set by ShelfBuilder, specified by user
    private float heightBudget;
    
    private ShelfBuilder shelfBuilder; // get from parent
    private ItemCategories itemCategories;
    private Dictionary<string, ItemPriceData> itemPriceData;
    
    public float itemOuterPadding;
    public float itemBackPadding;
    public float interItemPadding;
    public float fillFraction;
    private Material _airMaterial;
    private ItemCategory itemCategory;
    
    private float _bBoxPadding = 0.005f;

    private float direction;
    private List<GameObject> triggers = new();
    private LayerMask itemTriggerMask;
    private ShelfInfo _shelfInfo;

    private GameObject _priceTagPrefab;
    private float _priceTagHeight;
    
    public ShelfItemData shelfItemData;

    private ItemSpawnOption _itemSpawnOption;
    private bool spawnPriceTags = false;
    
    void Awake()
    {
        // triggers = new List<GameObject>();
        
        // Instantiate a DataHandler to access objects
        while (itemCategories == null)
        {
            itemCategories = DataHandler.Instance.itemCategories;
            itemPriceData = DataHandler.Instance.itemPriceData;
        }
        
        itemTriggerMask = LayerMask.NameToLayer("ItemBBox");
        
        shelfItemData = GetComponent<ShelfItemData>();
        
        UpdateShelfDimensions();
        
        /*
         * We don't spawn products immediately here 
         * because we process scaling/rotation first
         * in ShelfBuilder.cs, then we use SpawnProducts()
         */
    }

    public void Init(float distanceBetweenShelves, ItemSpawnOption spawnOption, bool _spawnPriceTags, ItemCategory category, Material airMaterial, GameObject priceTagPrefab, ShelfInfo shelfInfo)
    {
        heightBudget = distanceBetweenShelves;
        _itemSpawnOption = spawnOption;
        itemCategory = category;
        spawnPriceTags = _spawnPriceTags;
        _airMaterial = airMaterial;
        
        _priceTagPrefab = priceTagPrefab;
        _priceTagHeight = priceTagPrefab.GetComponent<Renderer>().bounds.size.y;
        _shelfInfo = shelfInfo;
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
        
        if (_itemSpawnOption == ItemSpawnOption.GenerateRandom)
        {
            // If we spawn items randomly, then just fill our ShelfItemData with random items
            shelfItemData.RandomFillFromCategory(
                itemCategory,
                interItemPadding, 
                widthBudget
            );
        }
        else if (_itemSpawnOption == ItemSpawnOption.GenerateRandomThenSave)
        {
            // Generate random items, then save to a JSON
            shelfItemData.RandomFillFromCategory(
                itemCategory,
                interItemPadding, 
                widthBudget
            );
            shelfItemData.SaveItemsToJson(_shelfInfo);
        }
        else if (_itemSpawnOption == ItemSpawnOption.ReadFromSave)
        {
            if (!shelfItemData.LoadItemsFromJson(_shelfInfo))
            {
                shelfItemData.RandomFillFromCategory(itemCategory, interItemPadding, widthBudget);
                shelfItemData.SaveItemsToJson(_shelfInfo);
            }
        }
        
        /*
         * Tracks how far along the shelf we are
         * Set to this initial value to prevent any awkward 
         * gaps at the end of the shelf
         * e.g.,
         *       xxxxx     -->   xxxxx
         *       ---------     ---------
         */
        float lengthwiseOffset = Math.Max(0, (widthBudget - shelfItemData.itemsTotalWidth)/2);
        
        bool firstItem = true;
        
        foreach (var shelfItem in shelfItemData.shelfItems)
        {
            GameObject product = shelfItem.prefab;

            float itemDepth = shelfItem.dimensions.depth;
            float itemWidth = shelfItem.dimensions.width;
            float itemHeight = shelfItem.dimensions.height;

            /*
             * Only worry about interItemPadding if it
             * isn't the leftmost/first item on the shelf
             */
            lengthwiseOffset += itemWidth/2 + (!firstItem ? interItemPadding : 0);

            /* Stop spawning if the item we're about to spawn is outside the shelf */
            if (lengthwiseOffset + itemWidth/2 + itemOuterPadding > widthBudget) break;

            int numRows = CalculateRows(itemDepth);
            int numStack = CalculateStackHeight(itemHeight, itemCategory);

            if (spawnPriceTags) SpawnPriceTag(shelfItem, lengthwiseOffset);

            for (int j = 0; j < numRows; j++)
            {
                for (int k = 0; k < numStack; k++)
                {
                    Vector3 spawnPosition =
                        GenerateSpawnPositionsOnShelf(
                            lengthwiseOffset,
                            itemDepth,
                            itemHeight,
                            j,
                            k
                        );

                    Quaternion aisleRot = Quaternion.Euler(0, DegreesToAisle(), 0);

                    InstanceData instanceData =
                        GenerateProductDrawData(product, spawnPosition, itemHeight);

                    GPUInstanceTracker.Instance.AddToInstance(
                        product.name,
                        product,
                        instanceData
                    );

                    GenerateBoundingBoxTriggerForItem(
                        spawnPosition,
                        itemHeight,
                        itemWidth,
                        itemDepth,
                        product.name,
                        instanceData,
                        aisleRot
                    );
                }
            }

            firstItem = false;
            lengthwiseOffset += itemWidth/2;
        }
    }

    private void SpawnPriceTag(RetailItemData shelfItem, float lengthwiseOffset)
    {
        Vector3 priceTagSpawnPos = transform.position 
                                   + transform.right * (lengthwiseOffset - widthBudget/2) * (ShelfIsFacingZ() ? -1 : 1) 
                                   + transform.forward * (depthBudget/2 + 0.001f)
                                   + transform.up * shelfWidth/2
                                   - transform.up * _priceTagHeight/2;
                    
        GameObject pt = Instantiate(
            _priceTagPrefab, 
            priceTagSpawnPos,
            Quaternion.LookRotation(-transform.up, transform.forward)
        );
                    
        PriceTag ptconfig = pt.GetComponent<PriceTag>();

        if (
            itemPriceData.TryGetValue(
                shelfItem.name,
                out ItemPriceData ptinfo)
            )
        {
            ptconfig.SetValues(
                shelfItem.name,
                ptinfo.pricePHP,
                ptinfo.netWeight
            );
        }
                    
        pt.isStatic = true;
        // Set name in Unity hierarchy, helpful when debugging
        pt.name = shelfItem.name + "_PRICE_TAG";
    }

    private void OnDestroy()
    {
        foreach (var trigger in triggers)
        {
            Destroy(trigger);
        }
    }

    InstanceData AdjustDrawDataIfPivotOnCenter(Transform lod0Transform, InstanceData data, float itemHeight)
    {
        Mesh mesh0 = lod0Transform != null ? lod0Transform.GetComponent<MeshFilter>()?.sharedMesh : null;

        if (mesh0 != null && lod0Transform.position != Vector3.zero)
        {
            /*
             * Our shelf position calcs assume the pivot
             * is at the item's bottom, not center, so adjust
             * for it. Without this, items spawn IN the shelves,
             * not ON. All LOD slots share the same spawn position,
             * so adjust them all together.
             */
            float adj = itemHeight / 2;
            data.lod0.position.y += adj;
            data.lod1.position.y += adj;
            data.lod2.position.y += adj;
            data.lod3.position.y += adj;
        }

        return data;
    }

    int CalculateRows(float itemWidth)
    {
        return (int) ((depthBudget-itemOuterPadding-itemBackPadding) /
                      (itemWidth + interItemPadding));
    }

    void GenerateBoundingBoxTriggerForItem(Vector3 spawnPosition, float itemHeight, float itemWidth, float itemDepth, string productName, InstanceData instanceData, Quaternion aisleRot)
    {
        GameObject itemTrigger = GameObject.CreatePrimitive(PrimitiveType.Cube);
        BoxCollider b = itemTrigger.GetComponent<BoxCollider>();
        ItemBBoxInfo itemBBoxInfo = itemTrigger.AddComponent<ItemBBoxInfo>();

        itemTrigger.tag = "RetailItemBBox";
        itemTrigger.AddComponent<OutlineFx.OutlineFx>();
        itemTrigger.AddComponent<OutlineController>();

        itemTrigger.transform.position = spawnPosition + new Vector3(0, itemHeight/2, 0);
        itemTrigger.layer = itemTriggerMask;

        itemTrigger.GetComponent<Renderer>().material = _airMaterial;

        itemBBoxInfo.itemId = productName;
        itemBBoxInfo.instanceData = instanceData;
        itemBBoxInfo.spawnRotation = aisleRot;

        if (DataHandler.Instance.enableShelfItemPhysics)
            itemTrigger.AddComponent<ItemBBoxPhysicsProxy>();

        b.name = productName;
        b.isTrigger = true;
        b.size = Vector3.one;

        itemTrigger.transform.localScale = new Vector3(
            (ShelfIsFacingZ() ? itemWidth : itemDepth) + _bBoxPadding,
            itemHeight + _bBoxPadding,
            (ShelfIsFacingZ() ? itemDepth : itemWidth) + _bBoxPadding
        );

        // itemTrigger.transform.SetParent(transform);
    }

    InstanceData GenerateProductDrawData(GameObject product, Vector3 spawnPosition, float itemHeight)
    {
        /*
         * Get the transforms of _LOD0–_LOD3 (if present).
         * Using the LOD child transforms rather than the product root ensures
         * correct local-vs-world coordinates for items with non-identity rotations/scales.
         */
        Transform prodChild = product.transform.GetChild(0);

        Transform lod0Transform = null;
        Transform lod1Transform = null;
        Transform lod2Transform = null;
        Transform lod3Transform = null;
        foreach (Transform child in prodChild)
        {
            if      (child.name.EndsWith("_LOD0")) lod0Transform = child;
            else if (child.name.EndsWith("_LOD1")) lod1Transform = child;
            else if (child.name.EndsWith("_LOD2")) lod2Transform = child;
            else if (child.name.EndsWith("_LOD3")) lod3Transform = child;
        }

        if (lod0Transform is null)
        {
            Debug.LogWarning("Could not find _LOD0 for object " + product.name);
            lod0Transform = prodChild;
        }

        Quaternion aisleRot = Quaternion.Euler(0, DegreesToAisle(), 0);

        // Build a LodTransform for the given child; falls back to lod0 if the child is absent.
        // Unused slots are safe to zero because the compute shader never indexes beyond num_lods-1.
        LodTransform MakeLodTransform(Transform t, Transform fallback)
        {
            Transform src = t != null ? t : fallback;
            Quaternion q = aisleRot * src.rotation;
            return new LodTransform
            {
                position = spawnPosition,
                rotation = new Vector4(q.x, q.y, q.z, q.w),
                scale    = src.lossyScale
            };
        }

        InstanceData data = new InstanceData
        {
            lod0 = MakeLodTransform(lod0Transform, lod0Transform),
            lod1 = MakeLodTransform(lod1Transform, lod0Transform),
            lod2 = MakeLodTransform(lod2Transform, lod0Transform),
            lod3 = MakeLodTransform(lod3Transform, lod0Transform),
        };

        return AdjustDrawDataIfPivotOnCenter(lod0Transform, data, itemHeight);
    }
    
    Vector3 GenerateSpawnPositionsOnShelf(float lengthwiseOffset, float itemDepth, float itemHeight, int rowNum, int stackNum, bool bBoxDepth = false)
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
            shelfPos.y + shelfWidth / 2 + (itemHeight + StackVerticalClearance) * stackNum,
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

    GameObject GetRandomProduct(ItemCategory itemCategory)
    {
        string[] categoryIds = itemCategories.Categories[(int)itemCategory].Items;
        string chosenId = categoryIds[Random.Range(0, categoryIds.Length)];
        
        // return Resources.Load<GameObject>("Prefabs/Products/LIBBYS_VIENNA_SAUSAGE_130G");
        // return Resources.Load<GameObject>("Prefabs/Products/NESTLE_KOKOKRUNCH_CHOCOLATE_330G");
        
        return Resources.Load<GameObject>("Prefabs/Products/" + chosenId);
    }

    int CalculateStackHeight(float itemHeight, ItemCategory category)
    {
        // TODO: can implement randomness for row front (i.e., iteration = 0)
        
        // stack only if of type "Can" or "Biscuit"
        if (category is ItemCategory.Can)
        {
            return (int)((heightBudget * fillFraction) / itemHeight);
        }
        
        return 1;
    }
}
