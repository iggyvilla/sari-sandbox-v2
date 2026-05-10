using UnityEngine;

public class HandItemDetector : MonoBehaviour
{
    public ItemBBoxInfo DetectedItemBBoxInfo { get; private set; }
    public GameObject DetectedItem { get; private set; }
    
    private OutlineController _itemOutlineController;
    private Collider _itemQueue;
    private bool _clearQueueFlag;
    
    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag("RetailItemBBox")) return;
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
    }

    private void OnTriggerExit(Collider other)
    {
        // Debug.Log($"EXIT: {other.gameObject.name}");
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
