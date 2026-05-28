using UnityEngine;

public class ItemBBoxInfo : MonoBehaviour
{
    // True when this component is on a dropped/physical item rather than a GPU-instanced shelf item.
    public bool isPhysicsObject;

    // When true, DeleteItem() returns the root (i.e., the physics
    // Game Object) to the pool instead of destroying it.
    // Set by ItemPoolingManager on pool-managed physics objects.
    public bool returnToPoolOnDelete;

    public string itemId;
    public InstanceData instanceData;
    // Aisle-facing rotation for the prefab root (not baked with LOD child offsets).
    // Used by ItemPhysicsProxy to spawn the physics prefab at the correct orientation.
    public Quaternion spawnRotation;

    // Called by ItemPhysicsProxy to unparent the BBox before the physics root is pooled.
    public System.Action onBeforeDelete;

    public void DeleteItem()
    {
        RetailItemRuntimeService.Instance.Delete(this);
    }

    private void OnDestroy()
    {
        RetailItemRuntimeService.RemoveShelfGpuInstanceForBBox(this);
    }
}
