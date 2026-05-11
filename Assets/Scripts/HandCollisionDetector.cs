using UnityEngine;

public class HandCollisionDetector : MonoBehaviour
{
    public ItemBBoxInfo DetectedItemBBoxInfo { get; private set; }
    public GameObject DetectedItem { get; private set; }
    public DoorHandle DetectedDoorHandle { get; private set; }
    public bool IsPointing { get; set; }

    public float itemPassThroughDistance = 0.1f;

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
        
        
        /* If the hand tapped a button */
        SimpleVRButton button = other.GetComponent<SimpleVRButton>();
        if (button != null)
        {
            button.Tapped();
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
            /*
             * If an item is queued (we exit item 1's bbox and collide with  
             * item 2), and we enter the currently selected bbox again,
             * clear the queue
             *
             * This fixes the weird edge case where we exit item 1's bbox
             * without touching any other items and suddenly an item beside it
             * is queued
             */
            if (DetectedItem == other.gameObject && _itemQueue) _itemQueue = null;
            
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

        if (DetectedItem != null)
        {
            if (BBoxDistanceToHand() > itemPassThroughDistance)
            {
                _itemQueue = null;
                _itemOutlineController = null;
                DetectedItem = null;
                DetectedItemBBoxInfo = null;
            }
        }
    }

    float BBoxDistanceToHand()
    {
        return Vector3.Distance(transform.position, DetectedItem.transform.position);
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
        
        // Need to get distance before clearing detected item
        float dist = BBoxDistanceToHand();
        
        _itemOutlineController = null;
        DetectedItem = null;
        DetectedItemBBoxInfo = null;
        
        if (_itemQueue != null)
        {
            _clearQueueFlag = true;
            
            if (dist < itemPassThroughDistance)
                OnTriggerEnter(_itemQueue);
        }
    }
}
