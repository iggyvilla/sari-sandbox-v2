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
    public InstanceLODData instanceLODData;
    // Aisle-facing rotation for the prefab root (not baked with LOD child offsets).
    // Used by ItemPhysicsProxy to spawn the physics prefab at the correct orientation.
    public Quaternion spawnRotation;

    // Called by ItemPhysicsProxy to unparent the BBox before the physics root is pooled.
    public System.Action onBeforeDelete;

    public void DeleteItem()
    {
        if (isPhysicsObject)
        {
            // Capture root before the callback potentially unparents ItemBBoxInfo.
            GameObject root = transform.root.gameObject;
            onBeforeDelete?.Invoke();
            if (returnToPoolOnDelete && ItemPoolingManager.Instance != null)
                ItemPoolingManager.Instance.ReturnToPool(itemId, root);
            else
                Destroy(root);
        }
        else
        {
            // Disable proxy immediately so the deferred Destroy doesn't let a late
            // OnTriggerEnter from the PhysicsActivationSphere re-spawn a physics object.
            var proxy = GetComponent<ItemBBoxPhysicsProxy>();
            if (proxy != null) proxy.enabled = false;
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
