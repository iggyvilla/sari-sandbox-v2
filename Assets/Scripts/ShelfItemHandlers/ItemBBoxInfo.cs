using System.Collections.Generic;
using UnityEngine;

public class ItemBBoxInfo : MonoBehaviour
{
    // True when this component is on a standalone dropped/physical item rather than
    // a GPU-instanced shelf item; deletion simply destroys the GameObject itself.
    public bool isSingularPhysicsObject;

    public string itemId;

    public List<InstanceLODData> itemDrawDatas = new();

    public void UpdateDrawDataList(List<InstanceLODData> drawDataList)
    {
        itemDrawDatas = drawDataList;
    }

    public void DeleteFrontmostItem()
    {
        if (isSingularPhysicsObject) { Destroy(gameObject); return; }

        if (itemDrawDatas.Count == 0) return;

        InstanceLODData frontItemDrawData = itemDrawDatas[0];

        BatchInstancer itemBatchInstancer =
            GPUInstanceTracker.Instance.GetBatchInstancerFromId(itemId);

        if (itemBatchInstancer != null)
        {
            itemBatchInstancer.RemoveSingleDrawData(frontItemDrawData);
        }

        itemDrawDatas.RemoveAt(0);
        
        /* If all items assigned to this BBox 
         * have been taken, destroy it */
        if (itemDrawDatas.Count == 0) Destroy(gameObject);
    }

    public void DeleteAllItems()
    {
        if (isSingularPhysicsObject) { Destroy(gameObject); return; }

        if (itemDrawDatas.Count == 0) return;

        BatchInstancer itemBatchInstancer =
            GPUInstanceTracker.Instance.GetBatchInstancerFromId(itemId);

        if (itemBatchInstancer != null)
        {
            itemBatchInstancer.RemoveDrawDataRange(itemDrawDatas);
        }

        itemDrawDatas.Clear();
    }
}
