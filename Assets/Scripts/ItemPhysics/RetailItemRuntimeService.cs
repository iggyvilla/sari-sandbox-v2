using System.Collections.Generic;
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
    public Transform originalBBoxParent;
    public Vector3 originalBBoxWorldPosition;
    public Quaternion originalBBoxWorldRotation;
    public Vector3 spawnedPosition;
    public Quaternion spawnedRotation;
    public Rigidbody physicsRigidbody;
    public Transform[] heldLayerTransforms;
    public int[] preHeldLayers;
    public MeshCollider[] heldMeshColliders;
    public bool[] preHeldMeshTriggers;

    public RuntimeRetailItem(string itemId, GameObject gameObject, RetailItemRuntimeState state)
    {
        this.itemId = itemId;
        this.gameObject = gameObject;
        this.state = state;
    }
}

public class RetailItemRuntimeService : MonoBehaviour
{
    [Tooltip("Frames the physics prefab stays visible after its GPU instance is restored, so the " +
             "handoff has no gap. Both draw during this overlap (double-blended transparency, " +
             "z-fighting), so keep it minimal and frame-based, not time-based.")]
    [SerializeField, Min(0)] private int restorePoolReturnDelayFrames = 1;

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

    // Non-creating lookup for cleanup/teardown paths.
    public static RetailItemRuntimeService TryGetInstance() => _instance != null ? _instance : null;

    private static readonly List<BoxCollider> BoxColliderBuffer = new();

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

        RemoveShelfGpuInstanceForBBox(bboxInfo);

        RuntimeRetailItem item = new RuntimeRetailItem(
            bboxInfo.itemId,
            physicsObj,
            RetailItemRuntimeState.PhysicsPreview)
        {
            expirationDateDecalId = bboxInfo.expirationDateDecalId,
            shelfBBoxInfo = bboxInfo,
            originalBBoxParent = bboxInfo.transform.parent,
            originalBBoxWorldPosition = bboxInfo.transform.position,
            originalBBoxWorldRotation = bboxInfo.transform.rotation,
            spawnedPosition = pos,
            spawnedRotation = rot,
            physicsRigidbody = physicsObj.GetComponent<Rigidbody>()
        };

        bboxInfo.transform.SetParent(physicsObj.transform, worldPositionStays: true);
        bboxInfo.isPhysicsObject = true;
        bboxInfo.returnToPoolOnDelete = true;
        bboxInfo.itemRoot = physicsObj;

        return item;
    }

    public void PreparePreviewForGrab(RuntimeRetailItem item)
    {
        if (item == null || item.shelfBBoxInfo == null) return;

        ItemBBoxInfo bboxInfo = item.shelfBBoxInfo;
        ReturnBBoxToShelfPose(item);
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
        ReturnBBoxToShelfPose(item);
        bboxInfo.ClearPhysicsState();

        RestoreGpuInstance(bboxInfo);

        // Returning the prefab in the same frame risks a gap before the GPU instance renders;
        // a few frames of overlap flickers instead. Hand off after the configured frame count.
        if (item.gameObject != null)
            StartCoroutine(ReturnToPoolDelayed(item.itemId, item.gameObject, restorePoolReturnDelayFrames));

        item.state = RetailItemRuntimeState.ShelfGpu;
        item.gameObject = null;
        item.physicsRigidbody = null;
    }

    // Re-parents the bbox where it was before activation so the virtual pool can reclaim it.
    private static void ReturnBBoxToShelfPose(RuntimeRetailItem item)
    {
        Transform bbox = item.shelfBBoxInfo.transform;
        bbox.SetParent(item.originalBBoxParent != null ? item.originalBBoxParent : null);
        bbox.SetPositionAndRotation(item.originalBBoxWorldPosition, item.originalBBoxWorldRotation);
    }

    private System.Collections.IEnumerator ReturnToPoolDelayed(string itemId, GameObject go, int delayFrames)
    {
        for (int i = 0; i < delayFrames; i++) yield return null;
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
        ReleaseHeldItem(item, bboxMaterial);
    }

    public void ThrowHeldItem(RuntimeRetailItem item, Material bboxMaterial, Vector3 impulse)
    {
        Rigidbody rb = ReleaseHeldItem(item, bboxMaterial);
        if (rb != null)
            rb.AddForce(impulse, ForceMode.Impulse);
    }

    private static Rigidbody ReleaseHeldItem(RuntimeRetailItem item, Material bboxMaterial)
    {
        if (item == null || item.gameObject == null) return null;

        Rigidbody rb = EnablePhysics(item);
        CreatePhysicsItemBBox(item.gameObject, item.itemId, item.expirationDateDecalId, bboxMaterial);
        item.state = RetailItemRuntimeState.Dropped;
        return rb;
    }

    public void Delete(ItemBBoxInfo bboxInfo)
    {
        if (bboxInfo == null) return;

        if (bboxInfo.isPhysicsObject)
        {
            // Not transform.root: items parented to a basket would resolve to the agent.
            GameObject root = ResolveItemRoot(bboxInfo);
            bboxInfo.onBeforeDelete?.Invoke();
            
            // If it's an item that turned physical, but didn't move, return it to the pool
            if (bboxInfo.returnToPoolOnDelete && ItemPoolingManager.Instance != null)
                ItemPoolingManager.Instance.ReturnToPool(bboxInfo.itemId, root);
            else
                Destroy(root);

            return;
        }

        ItemBBoxPhysicsProxy proxy = bboxInfo.Proxy;
        if (proxy != null) proxy.enabled = false;
        Destroy(bboxInfo.gameObject);
    }

    private static GameObject ResolveItemRoot(ItemBBoxInfo bboxInfo)
    {
        if (bboxInfo.itemRoot != null) return bboxInfo.itemRoot;

        Rigidbody rb = bboxInfo.GetComponentInParent<Rigidbody>();
        return rb != null ? rb.gameObject : bboxInfo.gameObject;
    }

    public static void RemoveShelfGpuInstanceForBBox(ItemBBoxInfo bboxInfo)
    {
        if (bboxInfo == null || bboxInfo.isPhysicsObject) return;

        GPUInstanceTracker tracker = GPUInstanceTracker.Instance;
        if (tracker == null) return;

        BatchInstancer itemBatchInstancer = tracker.GetBatchInstancerFromId(bboxInfo.itemId);
        if (itemBatchInstancer != null)
            itemBatchInstancer.RemoveSingleDrawData(bboxInfo.instanceData);
    }

    private GameObject CreateItemInstance(
        string itemId,
        string expirationDateDecalId,
        Vector3 position,
        Quaternion rotation,
        Transform parent)
    {
        GameObject item = ProductPrefabs.Spawn(itemId, position, rotation, parent);
        if (item != null) ExpirationDateDecalCatalog.ApplyTo(item, expirationDateDecalId);
        return item;
    }

    private static void RestoreGpuInstance(ItemBBoxInfo bboxInfo)
    {
        GameObject prefab = ProductPrefabs.Load(bboxInfo.itemId);
        GPUInstanceTracker tracker = GPUInstanceTracker.Instance;
        if (prefab != null && tracker != null)
            tracker.AddToInstance(bboxInfo.itemId, prefab, bboxInfo.instanceData);
    }

    private static Rigidbody EnablePhysics(RuntimeRetailItem item)
    {
        RestorePreHeldLayers(item);
        RestorePreHeldMeshTriggers(item);

        GameObject itemObject = item.gameObject;
        itemObject.transform.SetParent(null);
        SetSolidBoxCollidersEnabled(itemObject, true);

        Rigidbody rb = itemObject.GetComponent<Rigidbody>();
        if (rb != null)
            MakeDynamic(rb);

        return rb;
    }

    private static void ConfigurePhysicsForHeldItem(RuntimeRetailItem item)
    {
        GameObject itemObject = item.gameObject;
        Rigidbody rb = itemObject.GetComponent<Rigidbody>();
        if (rb != null)
            MakeKinematic(rb);

        // A held item is kinematic but keeps its solid colliders so it can still push
        // or contact other products. The HeldItem layer ignores the hand and body.
        SetSolidBoxCollidersEnabled(itemObject, true);
        ApplyHeldItemLayer(item);

        MeshCollider[] cols = itemObject.GetComponentsInChildren<MeshCollider>(true);
        bool[] wasTrigger = new bool[cols.Length];
        for (int i = 0; i < cols.Length; i++)
        {
            wasTrigger[i] = cols[i].isTrigger;
            cols[i].isTrigger = true;
        }

        item.heldMeshColliders = cols;
        item.preHeldMeshTriggers = wasTrigger;
    }

    private static void RestorePreHeldMeshTriggers(RuntimeRetailItem item)
    {
        MeshCollider[] cols = item.heldMeshColliders;
        bool[] wasTrigger = item.preHeldMeshTriggers;
        if (cols == null || wasTrigger == null) return;

        for (int i = 0; i < cols.Length && i < wasTrigger.Length; i++)
        {
            if (cols[i] != null)
                cols[i].isTrigger = wasTrigger[i];
        }

        item.heldMeshColliders = null;
        item.preHeldMeshTriggers = null;
    }

    // Zeroes velocity (if simulated) and freezes the body.
    internal static void MakeKinematic(Rigidbody rb)
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

    internal static void MakeDynamic(Rigidbody rb)
    {
        rb.isKinematic = false;
        rb.useGravity = true;
        rb.interpolation = RigidbodyInterpolation.Extrapolate;
    }

    internal static void SetSolidBoxCollidersEnabled(GameObject item, bool enabled)
    {
        item.GetComponentsInChildren(true, BoxColliderBuffer);
        foreach (BoxCollider collider in BoxColliderBuffer)
        {
            // Trigger boxes are sensors/bboxes rather than physical item bodies.
            if (!collider.isTrigger)
                collider.enabled = enabled;
        }

        BoxColliderBuffer.Clear();
    }

    private static int _heldItemLayer = int.MinValue;
    private static int HeldItemLayer =>
        _heldItemLayer != int.MinValue ? _heldItemLayer : (_heldItemLayer = LayerMask.NameToLayer("HeldItem"));

    private static void ApplyHeldItemLayer(RuntimeRetailItem item)
    {
        int heldItemLayer = HeldItemLayer;
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

        int heldItemLayer = HeldItemLayer;
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
        Transform lod0 = itemRoot.transform.childCount > 0
            ? LodHierarchy.ResolveLodTransforms(itemRoot)[0]
            : itemRoot.transform;
        Mesh mesh = lod0.TryGetComponent(out MeshFilter mf) ? mf.sharedMesh : null;
        Bounds meshBounds = mesh != null ? mesh.bounds : new Bounds(Vector3.zero, Vector3.one);

        ItemBBoxInfo itemBBoxInfo = ItemBBoxInfo.CreateBBoxObject("Cube", outlineEnabled: false, addPhysicsProxy: false);
        GameObject cube = itemBBoxInfo.gameObject;

        // Set world pose before parenting so the root's scale isn't applied twice.
        cube.transform.SetPositionAndRotation(lod0.TransformPoint(meshBounds.center), lod0.rotation);
        cube.transform.localScale = Vector3.Scale(lod0.lossyScale, meshBounds.size);
        cube.transform.SetParent(itemRoot.transform, worldPositionStays: true);

        Renderer renderer = itemBBoxInfo.Renderer;
        if (renderer != null && bboxMaterial != null)
            renderer.sharedMaterial = bboxMaterial;

        itemBBoxInfo.isPhysicsObject = true;
        itemBBoxInfo.itemRoot = itemRoot;
        itemBBoxInfo.itemId = itemId;
        itemBBoxInfo.expirationDateDecalId = expirationDateDecalId;

        return cube;
    }
}
