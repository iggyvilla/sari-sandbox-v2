using UnityEngine;

public class ItemBBoxInfo : MonoBehaviour
{
    // True when this component is on a dropped/physical item rather than a GPU-instanced shelf item.
    public bool isPhysicsObject;

    // When true, DeleteItem() returns the root (i.e., the physics
    // Game Object) to the pool instead of destroying it.
    // Set by ItemPoolingManager on pool-managed physics objects.
    public bool returnToPoolOnDelete;

    // Physics item this bbox belongs to while isPhysicsObject is set.
    public GameObject itemRoot;

    public string itemId;
    public string expirationDateDecalId;
    public InstanceData instanceData;
    public ShelfItemPhysicsStack PhysicsStack { get; set; }
    public VirtualItemBBoxRecord VirtualRecord { get; set; }
    public bool suppressGpuRemovalOnDestroy;

    // Original shelf/root spawn position before GPU-only pivot correction.
    public Vector3 physicsSpawnPosition;
    // Aisle-facing rotation for the prefab root (not baked with LOD child offsets).
    // Used by ItemBBoxPhysicsProxy to spawn the physics prefab at the correct orientation.
    public Quaternion spawnRotation;

    // Called by ItemBBoxPhysicsProxy to unparent the BBox before the physics root is pooled.
    public System.Action onBeforeDelete;

    private ItemBBoxPhysicsProxy _proxy;
    private BoxCollider _boxCollider;
    private Renderer _renderer;
    private OutlineFx.OutlineFx _outlineFx;
    private OutlineController _outlineController;

    public ItemBBoxPhysicsProxy Proxy => Cached(ref _proxy);
    public BoxCollider BoxCollider => Cached(ref _boxCollider);
    public Renderer Renderer => Cached(ref _renderer);
    public OutlineFx.OutlineFx OutlineFx => Cached(ref _outlineFx);
    public OutlineController OutlineController => Cached(ref _outlineController);

    private static int _bboxLayer = -1;
    public static int BBoxLayer => _bboxLayer >= 0 ? _bboxLayer : (_bboxLayer = LayerMask.NameToLayer("ItemBBox"));

    // Creates the trigger cube shared by shelf, virtual and dropped item bboxes.
    public static ItemBBoxInfo CreateBBoxObject(string objectName, bool outlineEnabled, bool addPhysicsProxy)
    {
        GameObject bbox = GameObject.CreatePrimitive(PrimitiveType.Cube);
        bbox.name = objectName;
        bbox.tag = "RetailItemBBox";
        bbox.layer = BBoxLayer;
        bbox.GetComponent<BoxCollider>().isTrigger = true;

        ItemBBoxInfo info = bbox.AddComponent<ItemBBoxInfo>();
        bbox.AddComponent<OutlineFx.OutlineFx>().enabled = outlineEnabled;
        bbox.AddComponent<OutlineController>();
        if (addPhysicsProxy)
            bbox.AddComponent<ItemBBoxPhysicsProxy>();

        return info;
    }

    public void DeleteItem()
    {
        RetailItemRuntimeService.Instance.Delete(this);
    }

    // Clears the physics-preview flags so the bbox acts as a plain shelf bbox again.
    public void ClearPhysicsState()
    {
        isPhysicsObject = false;
        returnToPoolOnDelete = false;
        itemRoot = null;
        onBeforeDelete = null;
    }

    // Clears all item data before the bbox is (re)used by the virtual pool.
    public void ClearItemState()
    {
        ClearPhysicsState();
        itemId = null;
        expirationDateDecalId = null;
        instanceData = default;
        physicsSpawnPosition = default;
        spawnRotation = Quaternion.identity;
        PhysicsStack = null;
        VirtualRecord = null;
    }

    private void OnDestroy()
    {
        NearbyItemBBoxManager.TryGetInstance()?.NotifyShelfBBoxDestroyed(this);

        if (!suppressGpuRemovalOnDestroy)
            RetailItemRuntimeService.RemoveShelfGpuInstanceForBBox(this);
    }

    private T Cached<T>(ref T field) where T : Component
    {
        if (field == null) TryGetComponent(out field);
        return field;
    }
}
