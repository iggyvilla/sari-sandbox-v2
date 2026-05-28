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
    public GameObject gameObject;
    public RetailItemRuntimeState state;
    public ItemBBoxInfo shelfBBoxInfo;
    public Vector3 originalBBoxWorldPosition;
    public Quaternion originalBBoxWorldRotation;
    public Vector3 spawnedPosition;
    public Quaternion spawnedRotation;
    public Rigidbody physicsRigidbody;

    public RuntimeRetailItem(string itemId, GameObject gameObject, RetailItemRuntimeState state)
    {
        this.itemId = itemId;
        this.gameObject = gameObject;
        this.state = state;
    }
}

public class RetailItemRuntimeService : MonoBehaviour
{
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

        string itemId = bboxInfo.itemId;
        bboxInfo.DeleteItem();

        GameObject item = CreateItemInstance(itemId, position, rotation, parent);
        if (item == null) return null;

        DisablePhysicsForHeldItem(item);
        item.transform.Rotate(localEulerOffset);

        return new RuntimeRetailItem(itemId, item, RetailItemRuntimeState.Held);
    }

    public RuntimeRetailItem ActivatePhysicsPreview(ItemBBoxInfo bboxInfo)
    {
        if (bboxInfo == null)
        {
            Debug.LogError("RetailItemRuntimeService: cannot activate physics for a null item bbox.");
            return null;
        }

        BatchInstancer bi = GPUInstanceTracker.Instance?.GetBatchInstancerFromId(bboxInfo.itemId);
        bi?.RemoveSingleDrawData(bboxInfo.instanceData);

        Vector3 pos = bboxInfo.instanceData.lod0.position;
        Quaternion rot = bboxInfo.spawnRotation;
        if (ItemPoolingManager.Instance == null)
        {
            Debug.LogError("RetailItemRuntimeService: ItemPoolingManager.Instance is missing.");
            return null;
        }

        GameObject physicsObj = ItemPoolingManager.Instance.GetOrCreate(bboxInfo.itemId, pos, rot);
        if (physicsObj == null) return null;

        RuntimeRetailItem item = new RuntimeRetailItem(
            bboxInfo.itemId,
            physicsObj,
            RetailItemRuntimeState.PhysicsPreview)
        {
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

    public void PreparePreviewForGrab(RuntimeRetailItem item, bool permanentlyPhysical)
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

        if (item.gameObject != null)
            ItemPoolingManager.Instance?.ReturnToPool(item.itemId, item.gameObject);

        RestoreGpuInstance(bboxInfo);

        item.state = RetailItemRuntimeState.ShelfGpu;
        item.gameObject = null;
        item.physicsRigidbody = null;
    }

    public void MarkPhysicsPreviewAsDropped(RuntimeRetailItem item)
    {
        if (item == null) return;
        item.state = RetailItemRuntimeState.Dropped;
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

        EnablePhysics(item.gameObject);
        CreatePhysicsItemBBox(item.gameObject, item.itemId, bboxMaterial);
        item.state = RetailItemRuntimeState.Dropped;
    }

    public void ThrowHeldItem(RuntimeRetailItem item, Vector3 impulse)
    {
        if (item == null || item.gameObject == null) return;

        Rigidbody rb = EnablePhysics(item.gameObject);
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

    private GameObject CreateItemInstance(string itemId, Vector3 position, Quaternion rotation, Transform parent)
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

    private static Rigidbody EnablePhysics(GameObject item)
    {
        item.transform.SetParent(null);

        Rigidbody rb = item.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = false;
            rb.useGravity = true;
            rb.interpolation = RigidbodyInterpolation.Extrapolate;
        }

        BoxCollider boxCollider = item.GetComponentInChildren<BoxCollider>();
        if (boxCollider != null) boxCollider.enabled = true;

        return rb;
    }

    private static void DisablePhysicsForHeldItem(GameObject item)
    {
        Rigidbody rb = item.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = true;
            rb.linearVelocity = Vector3.zero;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.None;
        }

        BoxCollider boxCollider = item.GetComponentInChildren<BoxCollider>();
        if (boxCollider != null) boxCollider.enabled = false;

        MeshCollider[] cols = item.GetComponentsInChildren<MeshCollider>(true);
        foreach (var c in cols)
            c.isTrigger = true;
    }

    private static GameObject CreatePhysicsItemBBox(GameObject itemRoot, string itemId, Material bboxMaterial)
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
