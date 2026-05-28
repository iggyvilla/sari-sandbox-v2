using System.Collections;
using UnityEngine;

// Attached to each shelf ItemBBox trigger alongside ItemBBoxInfo.
// When the agent's hand sphere enters, swaps the GPU-instanced mesh for a real physics
// prefab and parents this BBox under it so it follows the item if disturbed.
// On exit, either unparents and restores the BBox to its original shelf position
// (item barely moved), or keeps everything permanently as a grabbable dropped item.
[RequireComponent(typeof(ItemBBoxInfo))]
public class ItemBBoxPhysicsProxy : MonoBehaviour
{
    [SerializeField] private float positionThreshold = 0f;
    [SerializeField] private float rotationThreshold = 5f;

    private ItemBBoxInfo _bBoxInfo;
    private bool _permanentlyPhysical;
    private RuntimeRetailItem _runtimeItem;
    private Coroutine _settleCoroutine;

    void Awake()
    {
        _bBoxInfo = GetComponent<ItemBBoxInfo>();
    }
    
    // Runs upon entering the hand's trigger sphere
    void OnTriggerEnter(Collider other)
    {
        if (!DataHandler.Instance.enableShelfItemPhysics) return;
        if (_runtimeItem != null || _permanentlyPhysical) return;
        if (other.GetComponent<HandPhysicsSphere>() == null) return;

        ActivatePhysics();
    }
    
    // Runs upon exiting the hand's trigger sphere
    void OnTriggerExit(Collider other)
    {
        if (other.GetComponent<HandPhysicsSphere>() == null) return;
        if (_runtimeItem == null || _permanentlyPhysical) return;
        
        // Wait a few seconds (for physics to reach steady state), then 
        // evaluate if the item should become a physics item or stay GPU
        _settleCoroutine = StartCoroutine(WaitAndEvaluate());
    }

    void OnDestroy()
    {
        if (_settleCoroutine != null) StopCoroutine(_settleCoroutine);

        RetailItemRuntimeService.Instance.ReleaseActivePhysicsPreview(_runtimeItem);
    }

    private void ActivatePhysics()
    {
        _runtimeItem = RetailItemRuntimeService.Instance.ActivatePhysicsPreview(_bBoxInfo);
        if (_runtimeItem != null)
            _bBoxInfo.onBeforeDelete = OnBeforeItemGrabbed;
    }

    // Called by ItemBBoxInfo.DeleteItem() when the agent grabs the item mid-activation.
    private void OnBeforeItemGrabbed()
    {
        enabled = false; // prevent OnTriggerEnter from firing again before Destroy completes

        if (_settleCoroutine != null)
        {
            StopCoroutine(_settleCoroutine);
            _settleCoroutine = null;
        }

        RetailItemRuntimeService.Instance.PreparePreviewForGrab(_runtimeItem);
        _runtimeItem = null;
    }

    private IEnumerator WaitAndEvaluate()
    {
        yield return null;

        float elapsed = 0f;
        const float maxWait = 2f;
        Rigidbody physicsRb = _runtimeItem?.physicsRigidbody;
        while (physicsRb != null && !physicsRb.IsSleeping() && elapsed < maxWait)
        {
            yield return new WaitForSeconds(0.1f);
            elapsed += 0.1f;
        }

        if (_runtimeItem == null || _runtimeItem.gameObject == null)
        {
            _settleCoroutine = null;
            yield break;
        }

        float posDelta = Vector3.Distance(_runtimeItem.gameObject.transform.position, _runtimeItem.spawnedPosition);
        float rotDelta = Quaternion.Angle(_runtimeItem.gameObject.transform.rotation, _runtimeItem.spawnedRotation);
        
        // If the item, when settled, has moved past its threshold,
        // permanently stay as a physics item
        if (posDelta > positionThreshold || rotDelta > rotationThreshold)
        {
            _permanentlyPhysical = true;
            RetailItemRuntimeService.Instance.MarkPhysicsPreviewAsDropped(_runtimeItem);
        }
        else
        {
            RetailItemRuntimeService.Instance.RestorePhysicsPreviewToShelf(_runtimeItem);
            _runtimeItem = null;
        }

        _settleCoroutine = null;
    }
}
