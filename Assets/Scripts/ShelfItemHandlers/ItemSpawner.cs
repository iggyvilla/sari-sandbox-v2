using System.Collections.Generic;
using UnityEngine;

// Attached to the shelf side profile prefab
public class ItemSpawner : MonoBehaviour
{
    private const float StackVerticalClearance = 0f;

    private float widthBudget;
    private float depthBudget;
    private float shelfWidth;
    // Set by ShelfBuilder, specified by user
    private float heightBudget;
    
    private Dictionary<string, ItemPriceData> itemPriceData;
    
    public float itemOuterPadding;
    public float itemBackPadding;
    // Adjust these constants if you'd like the items to be less dense
    public const float InterItemPadding = 0.035f;
    private const float CanFillFraction = 0.5f;
    private Material _airMaterial;
    private ItemCategory itemCategory;
    
    private const float BBoxPadding = 0.005f;

    private float direction;
    private readonly List<GameObject> triggers = new();
    private readonly List<GameObject> _priceTags = new();
    private ShelfInfo _shelfInfo;

    private const float PriceTagScale = 1.1f;
    // Gap between the shelf lip and the price tag face.
    private const float PriceTagLipOffset = 0.001f;
    // Extra lift for upright tags on a fridge's lowest shelf.
    private const float FridgeBottomTagLift = 0.01f;
    private GameObject _priceTagPrefab;
    private float _priceTagHeight;
    private float _priceTagWidth;
    
    public ShelfItemData shelfItemData;

    private ItemSpawnOption _itemSpawnOption;
    private bool spawnPriceTags = false;
    private bool _spawnHingeDoors;
    private bool _initialized;
    
    void Awake()
    {
        shelfItemData = GetComponent<ShelfItemData>();
        if (shelfItemData == null)
        {
            Debug.LogError($"{nameof(ItemSpawner)} on {name}: missing {nameof(ShelfItemData)} component.");
            enabled = false;
        }

        // Products spawn later via SpawnProducts(), after ShelfBuilder scales/rotates the shelf.
    }

    public void Init(float distanceBetweenShelves, ItemSpawnOption spawnOption, bool _spawnPriceTags, ItemCategory category, Material airMaterial, GameObject priceTagPrefab, ShelfInfo shelfInfo, bool spawnHingeDoors)
    {
        TryInitialize();

        heightBudget = distanceBetweenShelves;
        _itemSpawnOption = spawnOption;
        itemCategory = category;
        spawnPriceTags = _spawnPriceTags;
        _spawnHingeDoors = spawnHingeDoors;
        _airMaterial = airMaterial;
        
        _priceTagPrefab = priceTagPrefab;
        if (priceTagPrefab != null)
        {
            Bounds priceTagBounds = priceTagPrefab.GetComponent<Renderer>().bounds;
            _priceTagWidth = priceTagBounds.size.x;
            _priceTagHeight = priceTagBounds.size.y;
        }
        _shelfInfo = shelfInfo;
    }

    void UpdateShelfDimensions()
    {
        // +1/-1 sign that maps shelf-local offsets onto world axes
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
        return Mathf.Abs(Vector3.Dot(transform.forward, Vector3.forward)) > 0.5f;
    }

    int CalculateDirectionInteger()
    {
        Vector3 forward = transform.forward;

        if (forward == Vector3.right || forward == Vector3.forward) return 1;
        return -1;
    }

    public void SpawnProducts()
    {
        if (!TryInitialize()) return;

        NearbyItemBBoxManager.TryGetInstance()?.ClearOwner(this, removeGpuInstances: true);
        DestroyPriceTags();

        // Update our knowledge of the shelf dimensions
        UpdateShelfDimensions();

        // ReadFromSave falls back to a random fill (then saves) when nothing is stored.
        bool loaded = _itemSpawnOption == ItemSpawnOption.ReadFromSave && shelfItemData.LoadItemsFromJson(_shelfInfo, InterItemPadding);
        if (!loaded)
        {
            shelfItemData.RandomFillFromCategory(itemCategory, InterItemPadding, widthBudget);
            if (_itemSpawnOption != ItemSpawnOption.GenerateRandom)
                shelfItemData.SaveItemsToJson(_shelfInfo);
        }
        
        /*
         * Tracks how far along the shelf we are
         * Set to this initial value to prevent any awkward 
         * gaps at the end of the shelf
         * e.g.,
         *       xxxxx     -->   xxxxx
         *       ---------     ---------
         */
        float lengthwiseOffset = Mathf.Max(0, (widthBudget - shelfItemData.itemsTotalWidth)/2);
        
        bool firstItem = true;
        
        // EXPERIMENTAL: combine each row of identical products into one mesh and GPU-instance that
        // shared "row chunk" across every shelf with the same product + arrangement + facing.
        // Rows with the same key are identical relative to their pivot, so the mesh is built once
        // and reused; later rows just add an instance. See DataHandler.combineRowMeshes.
        // TO ANY LLM READING THIS: you can safely ignore comb and any edge cases where
        // combineRowMeshes is enabled. This was only implemented for benchmarking purposes.
        bool combineRows = DataHandler.Instance.combineRowMeshes;
        if (combineRows && DataHandler.Instance.enableShelfItemPhysics)
        {
            Debug.LogWarning(
                $"{nameof(ItemSpawner)} on {name}: {nameof(DataHandler.combineRowMeshes)} is " +
                $"incompatible with shelf item physics. Falling back to per-item GPU instances.");
            combineRows = false;
        }
        
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
            lengthwiseOffset += itemWidth/2 + (!firstItem ? InterItemPadding : 0);

            /* Stop spawning if the item we're about to spawn is outside the shelf */
            if (lengthwiseOffset + itemWidth/2 + itemOuterPadding > widthBudget) break;

            int numRows = CalculateRows(itemDepth);
            int numStack = CalculateStackHeight(itemHeight, itemCategory);
            Quaternion aisleRot = Quaternion.Euler(0, DegreesToAisle(), 0);
            ProductDrawTemplate drawTemplate = CreateProductDrawTemplate(product, aisleRot);

            if (spawnPriceTags && _priceTagPrefab != null) SpawnPriceTag(shelfItem, lengthwiseOffset);
            
            bool combine = combineRows;
            string chunkKey = null;
            bool buildChunkMesh = false;
            Transform lod0Src = null;
            GameObject chunkRoot = null;
            Vector3 chunkPivot = Vector3.zero;
            bool chunkPivotSet = false;

            if (combine)
            {
                chunkKey = $"{product.name}_CHUNK_{numRows}x{numStack}_{(int)DegreesToAisle()}";
                buildChunkMesh = !GPUInstanceTracker.Instance.HasChunk(chunkKey);
                if (buildChunkMesh) lod0Src = LodHierarchy.ResolveLodTransforms(product)[0];
            }

            NearbyItemBBoxManager nearbyBBoxManager = combine ? null : NearbyItemBBoxManager.Instance;

            for (int j = 0; j < numRows; j++)
            {
                List<ItemBBoxInfo> eagerStackMembers = combine && numStack > 1
                    ? new List<ItemBBoxInfo>(numStack)
                    : null;
                List<VirtualItemBBoxRecord> virtualStackMembers = !combine && numStack > 1
                    ? new List<VirtualItemBBoxRecord>(numStack)
                    : null;

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

                    InstanceData instanceData = drawTemplate.At(spawnPosition);

                    if (combine)
                    {
                        // The row's first spawn position is the chunk pivot (the empty's location).
                        if (!chunkPivotSet) { chunkPivot = spawnPosition; chunkPivotSet = true; }

                        // Only build the shared mesh the first time this arrangement is seen.
                        if (buildChunkMesh)
                        {
                            if (chunkRoot == null)
                                chunkRoot = CreateChunkRoot(product.name, spawnPosition);

                            AddRowMeshChild(lod0Src, instanceData.lod0, chunkRoot.transform);
                        }
                    }
                    else
                    {
                        GPUInstanceTracker.Instance.AddToInstance(
                            product.name,
                            product,
                            instanceData
                        );
                    }

                    if (combine)
                    {
                        // Pass in spawnPosition because bounding box position
                        // calcs assumes mesh origin at bottom.
                        ItemBBoxInfo bboxInfo = GenerateBoundingBoxTriggerForItem(
                            spawnPosition,
                            itemHeight,
                            itemWidth,
                            itemDepth,
                            product.name,
                            instanceData,
                            aisleRot
                        );

                        eagerStackMembers?.Add(bboxInfo);
                    }
                    else
                    {
                        VirtualItemBBoxRecord record = CreateVirtualBBoxRecord(
                            spawnPosition,
                            itemHeight,
                            itemWidth,
                            itemDepth,
                            product.name,
                            instanceData,
                            aisleRot
                        );

                        nearbyBBoxManager.RegisterVirtualBBox(record);
                        virtualStackMembers?.Add(record);
                    }
                }

                if (eagerStackMembers != null)
                    new ShelfItemPhysicsStack(eagerStackMembers);
                if (virtualStackMembers != null)
                    nearbyBBoxManager.RegisterStackGroup(virtualStackMembers);
            }

            if (combine && chunkPivotSet)
            {
                if (buildChunkMesh && chunkRoot != null)
                    FinalizeChunk(chunkRoot, chunkKey, chunkPivot, ProductIsMultiMaterial(lod0Src));
                else
                    GPUInstanceTracker.Instance.AddChunkInstance(chunkKey, chunkPivot);
            }

            firstItem = false;
            lengthwiseOffset += itemWidth/2;
        }
    }

    /*
     * --- Row mesh-combining experiment (DataHandler.combineRowMeshes) ---
     *
     * Instead of registering each product with the GPU instancer, we instantiate the row's
     * products as children of an empty "chunk root" placed at the first spawn position, combine
     * them into one mesh (MeshCombiner), then hand that single mesh + the root's position to the
     * GPU instancer. Because the children are parented to the root, MeshCombiner's reset-to-origin
     * step bakes the verts PIVOT-RELATIVE to the root, so the chunk draws correctly at the root's
     * position with identity rotation / unit scale.
     */

    // Empty that acts as the combined row's pivot, placed at the row's first spawn position.
    GameObject CreateChunkRoot(string productName, Vector3 pivot)
    {
        GameObject root = new GameObject(productName + "_CHUNK");
        root.transform.position = pivot;
        // MeshCombiner requires these; the combined mesh lands on this MeshFilter.
        root.AddComponent<MeshFilter>();
        root.AddComponent<MeshRenderer>();
        return root;
    }

    // Instantiate the product's _LOD0 mesh at the exact transform the GPU would have drawn it,
    // parented to the chunk root so it gets folded into the combined mesh.
    void AddRowMeshChild(Transform lod0Src, LodTransform draw, Transform parent)
    {
        MeshFilter srcMf = lod0Src.GetComponent<MeshFilter>();
        MeshRenderer srcMr = lod0Src.GetComponent<MeshRenderer>();
        if (srcMf == null || srcMr == null) return;

        GameObject child = new GameObject("ROW_ITEM");
        child.transform.SetParent(parent, false);
        child.transform.SetPositionAndRotation(
            draw.position,
            new Quaternion(draw.rotation.x, draw.rotation.y, draw.rotation.z, draw.rotation.w));
        // Parent (chunk root) has unit scale, so localScale == the intended lossy scale.
        child.transform.localScale = draw.scale;

        child.AddComponent<MeshFilter>().sharedMesh = srcMf.sharedMesh;
        child.AddComponent<MeshRenderer>().sharedMaterials = srcMr.sharedMaterials;
    }

    // True if the product needs the multi-material combine path: MeshCombiner's single-material
    // path only keeps submesh 0 + the first material, which drops geometry/materials on items with
    // multiple submeshes (e.g. cans). The multi-material path preserves per-material submeshes.
    static bool ProductIsMultiMaterial(Transform lod0Src)
    {
        MeshFilter mf = lod0Src.GetComponent<MeshFilter>();
        MeshRenderer mr = lod0Src.GetComponent<MeshRenderer>();
        return (mr != null && mr.sharedMaterials.Length > 1)
            || (mf != null && mf.sharedMesh != null && mf.sharedMesh.subMeshCount > 1);
    }

    // Combine the chunk root's children into one mesh, register it under the shared key (which also
    // adds the first instance at the pivot), then discard the root.
    void FinalizeChunk(GameObject chunkRoot, string chunkKey, Vector3 pivot, bool multiMaterial)
    {
        MeshCombiner combiner = chunkRoot.AddComponent<MeshCombiner>();
        combiner.DestroyCombinedChildren = true;
        // Multi-submesh items must use the multi-material combine path or they lose submeshes.
        combiner.CreateMultiMaterialMesh = multiMaterial;
        combiner.CombineMeshes(false);

        Mesh combined = chunkRoot.GetComponent<MeshFilter>().sharedMesh;
        Material[] mats = chunkRoot.GetComponent<MeshRenderer>().sharedMaterials;

        if (combined != null)
            GPUInstanceTracker.Instance.AddCombinedChunk(chunkKey, combined, mats, pivot);

        // The BatchInstancer now owns the combined mesh; drop the root so its own MeshRenderer
        // doesn't double-render it.
        MeshRenderer rootRenderer = chunkRoot.GetComponent<MeshRenderer>();
        if (rootRenderer != null) rootRenderer.enabled = false;
        Destroy(chunkRoot);
    }

    private void SpawnPriceTag(RetailItemData shelfItem, float lengthwiseOffset)
    {
        Vector3 priceTagSpawnPos = transform.position 
                                   + transform.right * (lengthwiseOffset - widthBudget/2) * (ShelfIsFacingZ() ? -1 : 1) 
                                   + transform.forward * (depthBudget/2 + PriceTagLipOffset)
                                   + transform.up * shelfWidth/2
                                   - transform.up * _priceTagHeight/2;

        Quaternion priceTagRotation = Quaternion.LookRotation(-transform.up, transform.forward);

        // For a fridge's lowest shelf, the tag stands upright against the glass instead of
        // lying flat on the lip (handled inside TrySpawnBakedPriceTag).
        bool isFridgeBottomShelf = _spawnHingeDoors && _shelfInfo.subSubShelfId == 0;

        if (itemPriceData.ContainsKey(shelfItem.name) &&
            TrySpawnBakedPriceTag(shelfItem.name, priceTagSpawnPos, priceTagRotation, isFridgeBottomShelf))
        {
            return;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        GameObject pt = Instantiate(
            _priceTagPrefab, 
            priceTagSpawnPos,
            priceTagRotation
        );
                    
        PriceTag ptconfig = pt.GetComponent<PriceTag>();

        if (ptconfig != null && itemPriceData.TryGetValue(shelfItem.name, out ItemPriceData ptinfo))
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
        _priceTags.Add(pt);
#endif
    }

    private bool TrySpawnBakedPriceTag(string itemId, Vector3 position, Quaternion rotation, bool isFridgeBottomShelf)
    {
        if (!BakedPriceTag.TryGetSprite(itemId, out Sprite sprite))
        {
            return false;
        }

        // On a fridge's lowest shelf, raise the tag by half the shelf thickness so it clears
        // the lip and stands upright against the glass.
        if (isFridgeBottomShelf)
        {
            position += transform.up * (shelfWidth / 2f + FridgeBottomTagLift);
        }

        GameObject priceTag = new(itemId + "_PRICE_TAG");
        priceTag.transform.SetPositionAndRotation(position, rotation);
        priceTag.transform.Rotate(90, 180, 0);
        if (isFridgeBottomShelf)
        {
            priceTag.transform.Rotate(90, 0, 0);
        }
        priceTag.isStatic = true;

        SpriteRenderer spriteRenderer = priceTag.AddComponent<SpriteRenderer>();
        spriteRenderer.sprite = sprite;
        spriteRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        spriteRenderer.receiveShadows = false;
        spriteRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        spriteRenderer.sortingOrder = -1;

        priceTag.AddComponent<BakedPriceTag>();
        _priceTags.Add(priceTag);

        Vector2 spriteSize = sprite.bounds.size;
        if (spriteSize.x > 0f && spriteSize.y > 0f)
        {
            priceTag.transform.localScale = new Vector3(
                _priceTagWidth / spriteSize.x * PriceTagScale,
                _priceTagHeight / spriteSize.y * PriceTagScale,
                1f
            );
        }

        return true;
    }

    private void OnDestroy()
    {
        NearbyItemBBoxManager.TryGetInstance()?.ClearOwner(this, removeGpuInstances: true);
        DestroyPriceTags();

        foreach (var trigger in triggers)
        {
            if (trigger != null) Destroy(trigger);
        }
    }

    // Price tags live at scene root, so the spawner cleans up its own.
    private void DestroyPriceTags()
    {
        foreach (GameObject priceTag in _priceTags)
        {
            if (priceTag != null) Destroy(priceTag);
        }

        _priceTags.Clear();
    }

    int CalculateRows(float itemDepth)
    {
        return (int) ((depthBudget-itemOuterPadding-itemBackPadding) /
                      (itemDepth + InterItemPadding));
    }

    Vector3 BBoxCenter(Vector3 spawnPosition, float itemHeight)
    {
        // Cubes extrude from the center; item pivots are at the bottom.
        return spawnPosition + new Vector3(0, itemHeight / 2, 0);
    }

    Vector3 BBoxSize(float itemHeight, float itemWidth, float itemDepth)
    {
        bool facingZ = ShelfIsFacingZ();
        return new Vector3(
            (facingZ ? itemWidth : itemDepth) + BBoxPadding,
            itemHeight + BBoxPadding,
            (facingZ ? itemDepth : itemWidth) + BBoxPadding
        );
    }

    // Eager bbox used only by the combineRowMeshes benchmark path.
    ItemBBoxInfo GenerateBoundingBoxTriggerForItem(Vector3 spawnPosition, float itemHeight, float itemWidth, float itemDepth, string productName, InstanceData instanceData, Quaternion aisleRot)
    {
        ItemBBoxInfo itemBBoxInfo = ItemBBoxInfo.CreateBBoxObject(
            "Cube", outlineEnabled: true, addPhysicsProxy: DataHandler.Instance.enableShelfItemPhysics);
        GameObject bbox = itemBBoxInfo.gameObject;

        bbox.transform.position = BBoxCenter(spawnPosition, itemHeight);
        bbox.GetComponent<Renderer>().material = _airMaterial;

        itemBBoxInfo.itemId = productName;
        itemBBoxInfo.expirationDateDecalId = ExpirationDateDecalCatalog.GetRandomDecalId();
        itemBBoxInfo.instanceData = instanceData;
        itemBBoxInfo.physicsSpawnPosition = spawnPosition;
        itemBBoxInfo.spawnRotation = aisleRot;

        BoxCollider b = itemBBoxInfo.BoxCollider;
        b.name = productName;
        b.size = Vector3.one;

        bbox.transform.localScale = BBoxSize(itemHeight, itemWidth, itemDepth);

        triggers.Add(bbox);
        return itemBBoxInfo;
    }

    VirtualItemBBoxRecord CreateVirtualBBoxRecord(Vector3 spawnPosition, float itemHeight, float itemWidth, float itemDepth, string productName, InstanceData instanceData, Quaternion aisleRot)
    {
        return new VirtualItemBBoxRecord
        {
            itemId = productName,
            expirationDateDecalId = ExpirationDateDecalCatalog.GetRandomDecalId(),
            instanceData = instanceData,
            bboxCenter = BBoxCenter(spawnPosition, itemHeight),
            bboxSize = BBoxSize(itemHeight, itemWidth, itemDepth),
            physicsSpawnPosition = spawnPosition,
            spawnRotation = aisleRot,
            bboxMaterial = _airMaterial,
            ownerSpawner = this
        };
    }

    // Per-product draw data; only the positions change per instance.
    public readonly struct ProductDrawTemplate
    {
        private readonly InstanceData _data;
        // Per-LOD bottom-pivot y offset; NaN when no correction applies.
        private readonly Vector4 _bottomOffsets;

        public ProductDrawTemplate(InstanceData data, Vector4 bottomOffsets)
        {
            _data = data;
            _bottomOffsets = bottomOffsets;
        }

        public InstanceData At(Vector3 spawnPosition)
        {
            InstanceData data = _data;
            data.lod0.position = WithBottomOffset(spawnPosition, _bottomOffsets.x);
            data.lod1.position = WithBottomOffset(spawnPosition, _bottomOffsets.y);
            data.lod2.position = WithBottomOffset(spawnPosition, _bottomOffsets.z);
            data.lod3.position = WithBottomOffset(spawnPosition, _bottomOffsets.w);
            return data;
        }

        private static Vector3 WithBottomOffset(Vector3 position, float offset)
        {
            if (!float.IsNaN(offset)) position.y += offset;
            return position;
        }
    }

    public static ProductDrawTemplate CreateProductDrawTemplate(GameObject product, Quaternion aisleRot)
    {
        // LOD child transforms (not the root) give correct rotation/scale; lods[i] is never null.
        Transform[] lods = LodHierarchy.ResolveLodTransforms(product);

        LodTransform MakeLodTransform(Transform src)
        {
            Quaternion q = aisleRot * src.rotation;
            return new LodTransform
            {
                rotation = new Vector4(q.x, q.y, q.z, q.w),
                scale    = src.lossyScale
            };
        }

        InstanceData data = new InstanceData
        {
            lod0 = MakeLodTransform(lods[0]),
            lod1 = MakeLodTransform(lods[1]),
            lod2 = MakeLodTransform(lods[2]),
            lod3 = MakeLodTransform(lods[3]),
        };

        Vector4 bottomOffsets = new Vector4(
            BottomPivotOffset(lods[0]),
            BottomPivotOffset(lods[1]),
            BottomPivotOffset(lods[2]),
            BottomPivotOffset(lods[3]));

        return new ProductDrawTemplate(data, bottomOffsets);
    }

    // Shelf math assumes a bottom pivot; returns the lift for centre-pivot LODs, or NaN for none.
    static float BottomPivotOffset(Transform lodTransform)
    {
        if (lodTransform == null) return float.NaN;

        if (!lodTransform.TryGetComponent(out MeshFilter meshFilter) || meshFilter.sharedMesh == null)
        {
            Debug.Log("Cannot find mesh for: " + lodTransform.name);
            return float.NaN;
        }
        if (lodTransform.position == Vector3.zero) return float.NaN;

        return -meshFilter.sharedMesh.bounds.min.y * Mathf.Abs(lodTransform.lossyScale.y);
    }

    Vector3 GenerateSpawnPositionsOnShelf(float lengthwiseOffset, float itemDepth, float itemHeight, int rowNum, int stackNum)
    {
        Vector3 shelfPos = transform.position;
        
        float sideOffset =
            (widthBudget/2 - itemOuterPadding - lengthwiseOffset) *
            direction;

        float backOffset =
            (depthBudget/2 - ((itemDepth + InterItemPadding)
                              * (rowNum + 0.5f)) - itemOuterPadding) * direction;
        
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

    float DegreesToAisle()
    {
        Vector3 fwd = transform.forward;

        if (fwd == Vector3.left) return 0;
        if (fwd == Vector3.forward) return 90;
        if (fwd == Vector3.right) return 180;
        if (fwd == Vector3.back) return 270;
        return 0;
    }

    int CalculateStackHeight(float itemHeight, ItemCategory category)
    {
        // TODO: can implement randomness for row front (i.e., iteration = 0)
        
        // stack only if of type "Can"
        if (category is ItemCategory.Can)
        {
            return (int)((heightBudget * CanFillFraction) / itemHeight);
        }
        
        return 1;
    }

    private bool TryInitialize()
    {
        if (_initialized) return true;

        DataHandler dataHandler = DataHandler.Instance;
        if (dataHandler == null)
        {
            Debug.LogError($"{nameof(ItemSpawner)} on {name}: DataHandler.Instance is missing.");
            return false;
        }

        if (dataHandler.itemCategories == null || dataHandler.itemPriceData == null)
        {
            Debug.LogError($"{nameof(ItemSpawner)} on {name}: DataHandler item data is not loaded.");
            return false;
        }

        if (shelfItemData == null)
        {
            Debug.LogError($"{nameof(ItemSpawner)} on {name}: missing {nameof(ShelfItemData)} component.");
            enabled = false;
            return false;
        }

        itemPriceData = dataHandler.itemPriceData;

        _initialized = true;
        return true;
    }
}
