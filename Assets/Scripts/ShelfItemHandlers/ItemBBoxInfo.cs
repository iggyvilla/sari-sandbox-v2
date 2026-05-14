using UnityEngine;

public class ItemBBoxInfo : MonoBehaviour
{
    // True when this component is on a dropped/physical item rather than a GPU-instanced shelf item.
    public bool isPhysicsObject;

    // When true, DeleteItem() returns the root to the pool instead of destroying it.
    // Set by ItemPoolingManager on pool-managed physics objects.
    public bool returnToPoolOnDelete;

    public string itemId;
    public InstanceLODData instanceLODData;
    // Aisle-facing rotation for the prefab root (not baked with LOD child offsets).
    // Used by ItemPhysicsProxy to spawn the physics prefab at the correct orientation.
    public Quaternion spawnRotation;

    public void DeleteItem()
    {
        if (isPhysicsObject)
        {
            if (returnToPoolOnDelete && ItemPoolingManager.Instance != null)
                ItemPoolingManager.Instance.ReturnToPool(itemId, transform.root.gameObject);
            else
                Destroy(transform.root.gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    private void OnDestroy()
    {
        if (isPhysicsObject) return;

        BatchInstancer itemBatchInstancer =
            GPUInstanceTracker.Instance?.GetBatchInstancerFromId(itemId);

        if (itemBatchInstancer != null)
        {
            itemBatchInstancer.RemoveSingleDrawData(instanceLODData);
        }
    }
}
