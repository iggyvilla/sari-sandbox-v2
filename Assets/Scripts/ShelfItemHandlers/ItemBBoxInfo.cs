using System.Collections.Generic;
using UnityEngine;

public class ItemBBoxInfo : MonoBehaviour
{
    public List<DrawData> itemDrawDatas = new();

    public void UpdateDrawDataList(List<DrawData> drawDataList)
    {
        itemDrawDatas = drawDataList;
    }

    public void DeleteFrontmostItem()
    {
        if (itemDrawDatas.Count == 0) return;

        DrawData frontItemDrawData = itemDrawDatas[0];

        string myId = gameObject.name;

        BatchInstancer itemBatchInstancer =
            GPUInstanceTracker.Instance.GetBatchInstancerFromId(myId);

        if (itemBatchInstancer != null)
        {
            itemBatchInstancer.RemoveSingleDrawData(frontItemDrawData);
        }

        itemDrawDatas.RemoveAt(0);
    }

    public void DeleteAllItems()
    {
        if (itemDrawDatas.Count == 0) return;

        BatchInstancer itemBatchInstancer =
            GPUInstanceTracker.Instance.GetBatchInstancerFromId(gameObject.name);

        if (itemBatchInstancer != null)
        {
            itemBatchInstancer.ClearAllDrawData();
        }

        itemDrawDatas.Clear();
    }
}
