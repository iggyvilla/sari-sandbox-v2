using UnityEngine;

public class ItemBBoxInfo : MonoBehaviour
{
    // True when this component is on a dropped/physical item rather than a GPU-instanced shelf item.
    public bool isPhysicsObject;

    public string itemId;
    public InstanceLODData instanceLODData;

    public void DeleteItem()
    {
        Destroy(gameObject);
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
