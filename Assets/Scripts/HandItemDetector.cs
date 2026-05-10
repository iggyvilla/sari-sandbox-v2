using UnityEngine;

public class HandItemDetector : MonoBehaviour
{
    public ItemBBoxInfo DetectedItemBBoxInfo { get; private set; }
    public GameObject DetectedItem { get; private set; }
    public DoorHandle DetectedDoorHandle { get; private set; }

    private OutlineController _itemOutlineController;
    private Collider _itemQueue;
    private bool _clearQueueFlag;
    private bool _insideShelfDoor;

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("ShelfDoor"))
        {
            _insideShelfDoor = true;
            return;
        }

        DoorHandle dh = other.GetComponent<DoorHandle>();
        if (dh != null)
        {
            DetectedDoorHandle = dh;
            return;
        }

        if (!other.CompareTag("RetailItemBBox")) return;
        if (_insideShelfDoor) return;
        // Debug.Log($"COLLIDE: {other.gameObject.name}");
        
        /* If the hand is currently in a trigger box, queue the next item */
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
            _insideShelfDoor = false;
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
