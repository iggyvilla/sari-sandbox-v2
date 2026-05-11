using UnityEngine;

public class HandItemDetector : MonoBehaviour
{
    public ItemBBoxInfo DetectedItemBBoxInfo { get; private set; }
    public GameObject DetectedItem { get; private set; }
    public DoorHandle DetectedDoorHandle { get; private set; }
    public bool IsPointing { get; set; }

    private OutlineController _itemOutlineController;
    private Collider _itemQueue;
    private bool _clearQueueFlag;
    private int _shelfDoorOverlapCount;

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("ShelfDoor"))
        {
            _shelfDoorOverlapCount++;
            return;
        }

        DoorHandle dh = other.GetComponent<DoorHandle>();
        if (dh != null)
        {
            DetectedDoorHandle = dh;
            return;
        }

        if (!other.CompareTag("RetailItemBBox")) return;
        if (_shelfDoorOverlapCount > 0) return;
        
        /* If the hand is currently in a trigger box but enters
         * another trigger box, queue the next item */
        if (DetectedItem != null)
        {
            _itemQueue = other;
            return;
        } 
        
        if (_clearQueueFlag)
        {
            _itemQueue = null;
            _clearQueueFlag = false;
        }
        
        DetectedItem = other.gameObject;
        DetectedItemBBoxInfo = other.GetComponent<ItemBBoxInfo>();
        _itemOutlineController = DetectedItem.GetComponent<OutlineController>();
    }

    void Update()
    {
        if (_itemOutlineController != null) _itemOutlineController.OnGaze();
        if (DetectedDoorHandle != null) DetectedDoorHandle.OutlineController.OnGaze();
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.CompareTag("ShelfDoor"))
        {
            _shelfDoorOverlapCount = Mathf.Max(0, _shelfDoorOverlapCount - 1);
            return;
        }

        // Debug.Log($"EXIT: {other.gameObject.name}");
        DoorHandle dh = other.GetComponent<DoorHandle>();
        if (dh != null && dh == DetectedDoorHandle)
        {
            DetectedDoorHandle = null;
            return;
        }

        if (other.gameObject != DetectedItem) return;

        _itemOutlineController = null;
        DetectedItem = null;
        DetectedItemBBoxInfo = null;
        
        if (_itemQueue != null)
        {
            _clearQueueFlag = true;
            OnTriggerEnter(_itemQueue);
        }
    }
}
