using System.Collections.Generic;
using UnityEngine;

public class ItemBBoxInfo : MonoBehaviour
{
    public List<InstanceLODData> itemDrawDatas = new();

    public void UpdateDrawDataList(List<InstanceLODData> drawDataList)
    {
        itemDrawDatas = drawDataList;
    }

    public void DeleteFrontmostItem()
    {
        if (itemDrawDatas.Count == 0) return;

        InstanceLODData frontItemDrawData = itemDrawDatas[0];

        string myId = gameObject.name;

        BatchInstancer itemBatchInstancer =
            GPUInstanceTracker.Instance.GetBatchInstancerFromId(myId);

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
        if (itemDrawDatas.Count == 0) return;

        BatchInstancer itemBatchInstancer =
            GPUInstanceTracker.Instance.GetBatchInstancerFromId(gameObject.name);

        if (itemBatchInstancer != null)
        {
            itemBatchInstancer.RemoveDrawDataRange(itemDrawDatas);
        }

        itemDrawDatas.Clear();
    }
}
