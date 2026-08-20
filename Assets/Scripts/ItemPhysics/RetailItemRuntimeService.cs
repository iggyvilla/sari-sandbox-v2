using UnityEngine;

/*
 * Centralizes runtime retail item lifetimes.
 *
 * Shelf items start as GPU-instanced meshes with an ItemBBoxInfo trigger created by
 * ItemSpawner. From there, this service owns the destructive/constructive parts of
 * each state transition:
 *
 *   ShelfGpu -> PhysicsPreview
 *     ItemBBoxPhysicsProxy detects the hand activation sphere, then calls
 *     ActivatePhysicsPreview(). The service removes the GPU instance, gets a pooled
 *     physics prefab, and parents the shelf bbox under that prefab.
 *
 *   PhysicsPreview -> ShelfGpu
 *     ItemBBoxPhysicsProxy waits for the physics prefab to settle. If it barely moved,
 *     RestorePhysicsPreviewToShelf() returns the prefab to the pool, resets the bbox,
 *     and restores the GPU instance.
 *
 *   PhysicsPreview -> Dropped
 *     If the preview moved/rotated past the configured threshold, ItemBBoxPhysicsProxy
 *     calls MarkPhysicsPreviewAsDropped(). The real physics object stays in the world.
 *
 *   ShelfGpu/PhysicsPreview -> Held
 *     AgentControllerBase calls PickUpFromBBox() when the agent grabs an item. The
 *     service deletes or detaches the old bbox representation, instantiates the held
 *     prefab, disables physics on it, and parents it to the agent/hand.
 *
 *   Held -> Dropped
 *     AgentControllerBase calls DropHeldItem() or ThrowHeldItem(). The service detaches
 *     the prefab, enables physics, and creates the runtime ItemBBoxInfo used for later
 *     item detection/deletion.
 *
 * ItemBBoxInfo stays intentionally small: it stores item metadata and delegates delete
 * and shelf GPU cleanup back here. ItemPoolingManager still owns pooled prefab storage.
 */
public enum RetailItemRuntimeState
{
    ShelfGpu,
    PhysicsPreview,
    Held,
    Dropped
}

public sealed class RuntimeRetailItem
{
    public string itemId;
    public string expirationDateDecalId;
    public GameObject gameObject;
    public RetailItemRuntimeState state;
    public ItemBBoxInfo shelfBBoxInfo;
    public Vector3 originalBBoxWorldPosition;
    public Quaternion originalBBoxWorldRotation;
    public Vector3 spawnedPosition;
    public Quaternion spawnedRotation;
    public Rigidbody physicsRigidbody;
    public Transform[] heldLayerTransforms;
    public int[] preHeldLayers;

    public RuntimeRetailItem(string itemId, GameObject gameObject, RetailItemRuntimeState state)
    {
        this.itemId = itemId;
        this.gameObject = gameObject;
        this.state = state;
    }
}

public class RetailItemRuntimeService : MonoBehaviour
{
    [Tooltip("Delay before the physics prefab is returned to the pool when restoring " +
             "a preview to the shelf, giving the restored GPU instance a frame to render " +
             "and avoiding a flicker.")]
    [SerializeField] private float restorePoolReturnDelaySeconds = 0.05f;

    public static RetailItemRuntimeService Instance
    {
        get
        {
            if (_instance != null) return _instance;

            _instance = FindFirstObjectByType<RetailItemRuntimeService>();
            if (_instance != null) return _instance;

            GameObject go = new GameObject(nameof(RetailItemRuntimeService));
            _instance = go.AddComponent<RetailItemRuntimeService>();
            return _instance;
        }
    }

    private static RetailItemRuntimeService _instance;

    void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
    }

    public RuntimeRetailItem PickUpFromBBox(
        ItemBBoxInfo bboxInfo,
        Transform parent,
        Vector3 position,
        Quaternion rotation,
        Vector3 localEulerOffset)
    {
        if (bboxInfo == null)
        {
            Debug.LogError("RetailItemRuntimeService: cannot pick up a null item bbox.");
            return null;
        }

        if (bboxInfo.PhysicsStack != null &&
            DataHandler.Instance != null &&
            DataHandler.Instance.enableShelfItemPhysics &&
            !bboxInfo.PhysicsStack.ActivatePhysicsPreviews(settleWhenUnoccupied: true))
        {
            Debug.LogError("RetailItemRuntimeService: cannot activate the item's shelf stack for pickup.");
            return null;
        }

        string itemId = bboxInfo.itemId;
        string expirationDateDecalId = bboxInfo.expirationDateDecalId;
        bboxInfo.DeleteItem();

        GameObject item = CreateItemInstance(itemId, expirationDateDecalId, position, rotation, parent);
        if (item == null) return null;

        RuntimeRetailItem runtimeItem = new RuntimeRetailItem(
            itemId,
            item,
            RetailItemRuntimeState.Held)
        {
            expirationDateDecalId = expirationDateDecalId
        };

        ConfigurePhysicsForHeldItem(runtimeItem);
        item.transform.Rotate(localEulerOffset);

        return runtimeItem;
    }

    public RuntimeRetailItem ActivatePhysicsPreview(ItemBBoxInfo bboxInfo)
    {
        if (bboxInfo == null)
        {
            Debug.LogError("RetailItemRuntimeService: cannot activate physics for a null item bbox.");
            return null;
        }

        Vector3 pos = bboxInfo.physicsSpawnPosition;
        Quaternion rot = bboxInfo.spawnRotation;
        if (ItemPoolingManager.Instance == null)
        {
            Debug.LogError("RetailItemRuntimeService: ItemPoolingManager.Instance is missing.");
            return null;
        }

        GameObject physicsObj = ItemPoolingManager.Instance.GetOrCreate(bboxInfo.itemId, pos, rot);
        if (physicsObj == null) return null;
        ExpirationDateDecalCatalog.ApplyTo(physicsObj, bboxInfo.expirationDateDecalId);

        BatchInstancer bi = GPUInstanceTracker.Instance?.GetBatchInstancerFromId(bboxInfo.itemId);
        bi?.RemoveSingleDrawData(bboxInfo.instanceData);

        RuntimeRetailItem item = new RuntimeRetailItem(
            bboxInfo.itemId,
            physicsObj,
            RetailItemRuntimeState.PhysicsPreview)
        {
            expirationDateDecalId = bboxInfo.expirationDateDecalId,
            shelfBBoxInfo = bboxInfo,
            originalBBoxWorldPosition = bboxInfo.transform.position,
            originalBBoxWorldRotation = bboxInfo.transform.rotation,
            spawnedPosition = pos,
            spawnedRotation = rot,
            physicsRigidbody = physicsObj.GetComponent<Rigidbody>()
        };

        bboxInfo.transform.SetParent(physicsObj.transform, worldPositionStays: true);
        bboxInfo.isPhysicsObject = true;
        bboxInfo.returnToPoolOnDelete = true;

        return item;
    }

    public void PreparePreviewForGrab(RuntimeRetailItem item)
    {
        if (item == null || item.shelfBBoxInfo == null) return;

        ItemBBoxInfo bboxInfo = item.shelfBBoxInfo;
        bboxInfo.transform.SetParent(null);

        bboxInfo.transform.SetPositionAndRotation(
            item.originalBBoxWorldPosition,
            item.originalBBoxWorldRotation);
        bboxInfo.isPhysicsObject = false;
        bboxInfo.onBeforeDelete = null;
        Destroy(bboxInfo.gameObject);

        item.state = RetailItemRuntimeState.Held;
        item.gameObject = null;
        item.physicsRigidbody = null;
    }

    public void RestorePhysicsPreviewToShelf(RuntimeRetailItem item)
    {
        if (item == null || item.shelfBBoxInfo == null) return;

        ItemBBoxInfo bboxInfo = item.shelfBBoxInfo;
        bboxInfo.transform.SetParent(null);
        bboxInfo.transform.SetPositionAndRotation(
            item.originalBBoxWorldPosition,
            item.originalBBoxWorldRotation);
        ResetBBoxToShelf(bboxInfo);

        RestoreGpuInstance(bboxInfo);

        // The GPU instance won't actually render until the next frame. Returning the
        // physics prefab to the pool immediately would leave a one-frame gap where
        // neither representation is visible, causing a flicker. Defer the pool return
        // by a short delay so the GPU instance has rendered first.
        if (item.gameObject != null)
            StartCoroutine(ReturnToPoolDelayed(item.itemId, item.gameObject, restorePoolReturnDelaySeconds));

        item.state = RetailItemRuntimeState.ShelfGpu;
        item.gameObject = null;
        item.physicsRigidbody = null;
    }

    private System.Collections.IEnumerator ReturnToPoolDelayed(string itemId, GameObject go, float delaySeconds)
    {
        yield return new WaitForSeconds(delaySeconds);
        if (go != null)
            ItemPoolingManager.Instance?.ReturnToPool(itemId, go);
    }

    public void MarkPhysicsPreviewAsDropped(RuntimeRetailItem item)
    {
        if (item == null) return;
        item.state = RetailItemRuntimeState.Dropped;
        NearbyItemBBoxManager.TryGetInstance()?.NotifyBBoxBecameDropped(item.shelfBBoxInfo);
    }

    public void ReleaseActivePhysicsPreview(RuntimeRetailItem item)
    {
        if (item == null || item.state != RetailItemRuntimeState.PhysicsPreview || item.gameObject == null)
            return;

        if (item.shelfBBoxInfo != null)
            item.shelfBBoxInfo.transform.SetParent(null);

        ItemPoolingManager.Instance?.ReturnToPool(item.itemId, item.gameObject);
        item.gameObject = null;
        item.physicsRigidbody = null;
    }

    public void DropHeldItem(RuntimeRetailItem item, Material bboxMaterial)
    {
        if (item == null || item.gameObject == null) return;

        EnablePhysics(item);
        CreatePhysicsItemBBox(item.gameObject, item.itemId, item.expirationDateDecalId, bboxMaterial);
        item.state = RetailItemRuntimeState.Dropped;
    }

    public void ThrowHeldItem(RuntimeRetailItem item, Material bboxMaterial, Vector3 impulse)
    {
        if (item == null || item.gameObject == null) return;

        Rigidbody rb = EnablePhysics(item);
        CreatePhysicsItemBBox(item.gameObject, item.itemId, item.expirationDateDecalId, bboxMaterial);
        if (rb != null)
            rb.AddForce(impulse, ForceMode.Impulse);

        item.state = RetailItemRuntimeState.Dropped;
    }

    public void Delete(ItemBBoxInfo bboxInfo)
    {
        if (bboxInfo == null) return;

        if (bboxInfo.isPhysicsObject)
        {
            GameObject root = bboxInfo.transform.root.gameObject;
            bboxInfo.onBeforeDelete?.Invoke();
            
            // If it's an item that turned physical, but didn't move, return it to the pool
            if (bboxInfo.returnToPoolOnDelete && ItemPoolingManager.Instance != null)
                ItemPoolingManager.Instance.ReturnToPool(bboxInfo.itemId, root);
            else
                Destroy(root);

            return;
        }

        var proxy = bboxInfo.GetComponent<ItemBBoxPhysicsProxy>();
        if (proxy != null) proxy.enabled = false;
        Destroy(bboxInfo.gameObject);
    }

    public static void RemoveShelfGpuInstanceForBBox(ItemBBoxInfo bboxInfo)
    {
        if (bboxInfo == null || bboxInfo.isPhysicsObject) return;

        BatchInstancer itemBatchInstancer =
            GPUInstanceTracker.Instance?.GetBatchInstancerFromId(bboxInfo.itemId);

        itemBatchInstancer?.RemoveSingleDrawData(bboxInfo.instanceData);
    }

    private GameObject CreateItemInstance(
        string itemId,
        string expirationDateDecalId,
        Vector3 position,
        Quaternion rotation,
        Transform parent)
    {
        GameObject prefab = Resources.Load<GameObject>("Prefabs/Products/" + itemId);
        if (prefab == null)
        {
            Debug.LogError($"RetailItemRuntimeService: prefab not found for {itemId}");
            return null;
        }

        GameObject item = Instantiate(prefab, position, rotation, parent);
        item.name = itemId;
        item.tag = "RetailItem";
        ExpirationDateDecalCatalog.ApplyTo(item, expirationDateDecalId);
        return item;
    }

    private static void ResetBBoxToShelf(ItemBBoxInfo bboxInfo)
    {
        bboxInfo.isPhysicsObject = false;
        bboxInfo.returnToPoolOnDelete = false;
        bboxInfo.onBeforeDelete = null;
    }

    private static void RestoreGpuInstance(ItemBBoxInfo bboxInfo)
    {
        GameObject prefab = Resources.Load<GameObject>("Prefabs/Products/" + bboxInfo.itemId);
        if (prefab != null)
            GPUInstanceTracker.Instance?.AddToInstance(bboxInfo.itemId, prefab, bboxInfo.instanceData);
    }

    private static Rigidbody EnablePhysics(RuntimeRetailItem item)
    {
        RestorePreHeldLayers(item);

        GameObject itemObject = item.gameObject;
        itemObject.transform.SetParent(null);
        SetSolidBoxCollidersEnabled(itemObject, true);

        Rigidbody rb = itemObject.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = false;
            rb.useGravity = true;
            rb.interpolation = RigidbodyInterpolation.Extrapolate;
        }

        return rb;
    }

    private static void ConfigurePhysicsForHeldItem(RuntimeRetailItem item)
    {
        GameObject itemObject = item.gameObject;
        Rigidbody rb = itemObject.GetComponent<Rigidbody>();
        if (rb != null)
        {
            if (!rb.isKinematic)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            rb.isKinematic = true;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.None;
        }

        // A held item is kinematic but keeps its solid colliders so it can still push
        // or contact other products. The HeldItem layer ignores the hand and body.
        SetSolidBoxCollidersEnabled(itemObject, true);
        ApplyHeldItemLayer(item);

        MeshCollider[] cols = itemObject.GetComponentsInChildren<MeshCollider>(true);
        foreach (var c in cols)
            c.isTrigger = true;
    }

    internal static void SetSolidBoxCollidersEnabled(GameObject item, bool enabled)
    {
        BoxCollider[] colliders = item.GetComponentsInChildren<BoxCollider>(true);
        foreach (BoxCollider collider in colliders)
        {
            // Trigger boxes are sensors/bboxes rather than physical item bodies.
            if (!collider.isTrigger)
                collider.enabled = enabled;
        }
    }

    private static void ApplyHeldItemLayer(RuntimeRetailItem item)
    {
        int heldItemLayer = LayerMask.NameToLayer("HeldItem");
        if (heldItemLayer < 0)
        {
            Debug.LogError("RetailItemRuntimeService: HeldItem layer is missing.");
            return;
        }

        Transform[] transforms = item.gameObject.GetComponentsInChildren<Transform>(true);
        int[] originalLayers = new int[transforms.Length];
        for (int i = 0; i < transforms.Length; i++)
        {
            int originalLayer = transforms[i].gameObject.layer;
            originalLayers[i] = originalLayer == heldItemLayer ? 0 : originalLayer;
            transforms[i].gameObject.layer = heldItemLayer;
        }

        item.heldLayerTransforms = transforms;
        item.preHeldLayers = originalLayers;
    }

    private static void RestorePreHeldLayers(RuntimeRetailItem item)
    {
        Transform[] transforms = item.heldLayerTransforms;
        int[] layers = item.preHeldLayers;
        if (transforms == null || layers == null)
        {
            ClearHeldItemLayer(item.gameObject);
            return;
        }

        int count = Mathf.Min(transforms.Length, layers.Length);
        for (int i = 0; i < count; i++)
        {
            if (transforms[i] != null)
                transforms[i].gameObject.layer = layers[i];
        }

        item.heldLayerTransforms = null;
        item.preHeldLayers = null;
    }

    internal static void ClearHeldItemLayer(GameObject item)
    {
        if (item == null) return;

        int heldItemLayer = LayerMask.NameToLayer("HeldItem");
        if (heldItemLayer < 0) return;
        if (item.layer != heldItemLayer) return;

        Transform[] transforms = item.GetComponentsInChildren<Transform>(true);
        foreach (Transform child in transforms)
        {
            if (child.gameObject.layer == heldItemLayer)
                child.gameObject.layer = 0;
        }
    }

    private static GameObject CreatePhysicsItemBBox(
        GameObject itemRoot,
        string itemId,
        string expirationDateDecalId,
        Material bboxMaterial)
    {
        Transform lod0 = FindLOD0(itemRoot);
        MeshFilter mf = lod0.GetComponent<MeshFilter>();
        Bounds meshBounds = mf != null ? mf.sharedMesh.bounds : new Bounds(Vector3.zero, Vector3.one);

        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.transform.SetParent(itemRoot.transform, worldPositionStays: true);
        cube.transform.position = lod0.TransformPoint(meshBounds.center);
        cube.transform.rotation = lod0.rotation;
        cube.transform.localScale = Vector3.Scale(lod0.lossyScale, meshBounds.size);

        cube.GetComponent<BoxCollider>().isTrigger = true;
        MeshRenderer renderer = cube.GetComponent<MeshRenderer>();
        if (renderer != null && bboxMaterial != null)
            renderer.sharedMaterial = bboxMaterial;

        cube.tag = "RetailItemBBox";
        cube.layer = LayerMask.NameToLayer("ItemBBox");
        cube.AddComponent<OutlineFx.OutlineFx>().enabled = false;
        cube.AddComponent<OutlineController>();

        ItemBBoxInfo itemBBoxInfo = cube.AddComponent<ItemBBoxInfo>();
        itemBBoxInfo.isPhysicsObject = true;
        itemBBoxInfo.itemId = itemId;
        itemBBoxInfo.expirationDateDecalId = expirationDateDecalId;

        return cube;
    }

    private static Transform FindLOD0(GameObject item)
    {
        if (item.transform.childCount == 0) return item.transform;

        Transform prodChild = item.transform.GetChild(0);
        foreach (Transform t in prodChild)
        {
            if (t.name.EndsWith("_LOD0"))
                return t;
        }

        return prodChild;
    }
}
