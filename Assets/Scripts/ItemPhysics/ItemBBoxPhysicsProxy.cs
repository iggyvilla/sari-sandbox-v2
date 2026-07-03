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
    [SerializeField] private float positionThreshold = 0.01f;
    [SerializeField] private float rotationThreshold = 5f;

    private ItemBBoxInfo _bBoxInfo;
    private bool _permanentlyPhysical;
    private RuntimeRetailItem _runtimeItem;
    private Coroutine _settleCoroutine;

    internal bool HasPhysicsPreview =>
        _runtimeItem != null &&
        _runtimeItem.state == RetailItemRuntimeState.PhysicsPreview &&
        _runtimeItem.gameObject != null;
    internal bool CanReturnToVirtualPool =>
        !_permanentlyPhysical &&
        _runtimeItem == null &&
        _settleCoroutine == null &&
        (_bBoxInfo == null || !_bBoxInfo.isPhysicsObject);
    internal ItemBBoxInfo BBoxInfo => _bBoxInfo != null ? _bBoxInfo : GetComponent<ItemBBoxInfo>();

    void Awake()
    {
        _bBoxInfo = GetComponent<ItemBBoxInfo>();
    }
    
    // Runs upon entering the hand's trigger sphere
    void OnTriggerEnter(Collider other)
    {
        if (!DataHandler.Instance.enableShelfItemPhysics) return;
        if (other.GetComponent<HandPhysicsSphere>() == null) return;

        if (_bBoxInfo.PhysicsStack != null)
        {
            _bBoxInfo.PhysicsStack.OnHandEnter(this, other);
            return;
        }

        if (_permanentlyPhysical) return;

        CancelSettleEvaluation();
        EnsurePhysicsPreview();
    }
    
    // Runs upon exiting the hand's trigger sphere
    void OnTriggerExit(Collider other)
    {
        if (other.GetComponent<HandPhysicsSphere>() == null) return;

        if (_bBoxInfo.PhysicsStack != null)
        {
            _bBoxInfo.PhysicsStack.OnHandExit(this, other);
            return;
        }

        if (_runtimeItem == null || _permanentlyPhysical) return;
        
        // Wait a few seconds (for physics to reach steady state), then 
        // evaluate if the item should become a physics item or stay GPU
        if (_settleCoroutine == null)
            _settleCoroutine = StartCoroutine(WaitAndEvaluate());
    }

    void OnDestroy()
    {
        CancelSettleEvaluation();
        _bBoxInfo?.PhysicsStack?.OnMemberRemoved(this);

        if (_runtimeItem != null)
            RetailItemRuntimeService.Instance.ReleaseActivePhysicsPreview(_runtimeItem);
        _runtimeItem = null;
    }

    internal bool EnsurePhysicsPreview()
    {
        if (_runtimeItem != null || _permanentlyPhysical) return true;

        _runtimeItem = RetailItemRuntimeService.Instance.ActivatePhysicsPreview(_bBoxInfo);
        if (_runtimeItem != null)
            _bBoxInfo.onBeforeDelete = OnBeforeItemGrabbed;

        return _runtimeItem != null;
    }

    internal void ResetForVirtualPoolReuse(bool enableShelfPhysics)
    {
        CancelSettleEvaluation();
        _bBoxInfo = GetComponent<ItemBBoxInfo>();
        _permanentlyPhysical = false;
        _runtimeItem = null;
        enabled = enableShelfPhysics;
    }

    internal void ResetForVirtualPoolRelease()
    {
        CancelSettleEvaluation();
        _permanentlyPhysical = false;
        _runtimeItem = null;
        enabled = false;
    }

    internal void ReleasePreviewForVirtualCleanup()
    {
        CancelSettleEvaluation();

        if (_bBoxInfo != null)
            _bBoxInfo.PhysicsStack?.OnMemberRemoved(this);

        if (_runtimeItem != null)
            RetailItemRuntimeService.Instance.ReleaseActivePhysicsPreview(_runtimeItem);
        _runtimeItem = null;
        _permanentlyPhysical = false;

        if (_bBoxInfo != null)
        {
            _bBoxInfo.isPhysicsObject = false;
            _bBoxInfo.returnToPoolOnDelete = false;
            _bBoxInfo.onBeforeDelete = null;
        }

        enabled = false;
    }

    // Called by ItemBBoxInfo.DeleteItem() when the agent grabs the item mid-activation.
    private void OnBeforeItemGrabbed()
    {
        enabled = false; // prevent OnTriggerEnter from firing again before Destroy completes

        CancelSettleEvaluation();
        _bBoxInfo.PhysicsStack?.OnMemberRemoved(this);

        RetailItemRuntimeService.Instance.PreparePreviewForGrab(_runtimeItem);
        _runtimeItem = null;
    }

    private IEnumerator WaitAndEvaluate()
    {
        yield return null;

        float elapsed = 0f;
        const float maxWait = 2f;
        while (!IsPhysicsPreviewSleeping() && elapsed < maxWait)
        {
            yield return new WaitForSeconds(0.1f);
            elapsed += 0.1f;
        }

        if (_runtimeItem == null || _runtimeItem.gameObject == null)
        {
            _settleCoroutine = null;
            yield break;
        }

        // If the item, when settled, has moved past its threshold,
        // permanently stay as a physics item
        if (HasPhysicsPreviewMovedPastThreshold())
        {
            MarkPhysicsPreviewAsDropped();
        }
        else
        {
            RestorePhysicsPreviewToShelf();
        }

        _settleCoroutine = null;
    }

    internal bool IsPhysicsPreviewSleeping()
    {
        Rigidbody physicsRb = _runtimeItem?.physicsRigidbody;
        return physicsRb == null || physicsRb.IsSleeping();
    }

    internal bool HasPhysicsPreviewMovedPastThreshold()
    {
        if (!HasPhysicsPreview) return false;

        float posDelta = Vector3.Distance(_runtimeItem.gameObject.transform.position, _runtimeItem.spawnedPosition);
        float rotDelta = Quaternion.Angle(_runtimeItem.gameObject.transform.rotation, _runtimeItem.spawnedRotation);
        return posDelta > positionThreshold || rotDelta > rotationThreshold;
    }

    internal void MarkPhysicsPreviewAsDropped()
    {
        if (!HasPhysicsPreview) return;

        _permanentlyPhysical = true;
        RetailItemRuntimeService.Instance.MarkPhysicsPreviewAsDropped(_runtimeItem);
    }

    internal void RestorePhysicsPreviewToShelf()
    {
        if (!HasPhysicsPreview) return;

        RetailItemRuntimeService.Instance.RestorePhysicsPreviewToShelf(_runtimeItem);
        _runtimeItem = null;
    }

    private void CancelSettleEvaluation()
    {
        if (_settleCoroutine == null) return;

        StopCoroutine(_settleCoroutine);
        _settleCoroutine = null;
    }
}
