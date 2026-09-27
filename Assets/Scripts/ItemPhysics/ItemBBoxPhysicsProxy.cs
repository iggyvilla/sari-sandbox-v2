using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Attached to each shelf ItemBBox trigger alongside ItemBBoxInfo.
// When the agent's hand sphere enters, swaps the GPU-instanced mesh for a real physics
// prefab and parents this BBox under it so it follows the item if disturbed.
// On exit, either unparents and restores the BBox to its original shelf position
// (item barely moved), or keeps everything permanently as a grabbable dropped item.
[RequireComponent(typeof(ItemBBoxInfo))]
public class ItemBBoxPhysicsProxy : MonoBehaviour
{
    private const float PositionThreshold = 0.01f;
    private const float RotationThresholdDegrees = 5f;

    private ItemBBoxInfo _bBoxInfo;
    private bool _permanentlyPhysical;
    private RuntimeRetailItem _runtimeItem;
    private Coroutine _settleCoroutine;
    // Hand spheres currently inside this bbox (one per agent hand).
    private readonly HashSet<Collider> _handOverlaps = new();

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
        // Unity sends trigger events to disabled behaviours too.
        if (!enabled) return;
        if (DataHandler.Instance == null || !DataHandler.Instance.enableShelfItemPhysics) return;
        if (!other.TryGetComponent(out HandPhysicsSphere _)) return;

        if (_bBoxInfo.PhysicsStack != null)
        {
            _bBoxInfo.PhysicsStack.OnHandEnter(this, other);
            return;
        }

        if (_permanentlyPhysical) return;

        _handOverlaps.Add(other);
        CancelSettleEvaluation();
        EnsurePhysicsPreview();
    }
    
    // Runs upon exiting the hand's trigger sphere
    void OnTriggerExit(Collider other)
    {
        if (!enabled) return;
        if (!other.TryGetComponent(out HandPhysicsSphere _)) return;

        if (_bBoxInfo.PhysicsStack != null)
        {
            _bBoxInfo.PhysicsStack.OnHandExit(this, other);
            return;
        }

        _handOverlaps.Remove(other);
        _handOverlaps.RemoveWhere(c => c == null);
        if (_handOverlaps.Count > 0) return;

        if (_runtimeItem == null || _permanentlyPhysical) return;
        
        // Wait a few seconds (for physics to reach steady state), then 
        // evaluate if the item should become a physics item or stay GPU
        if (_settleCoroutine == null)
            _settleCoroutine = StartCoroutine(WaitAndEvaluate());
    }

    void OnDestroy()
    {
        CancelSettleEvaluation();
        if (_bBoxInfo != null)
            _bBoxInfo.PhysicsStack?.OnMemberRemoved(this);

        ReleaseRuntimeItem();
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
        ResetPoolState();
        _bBoxInfo = GetComponent<ItemBBoxInfo>();
        enabled = enableShelfPhysics;
    }

    internal void ResetForVirtualPoolRelease()
    {
        ResetPoolState();
        enabled = false;
    }

    internal void ReleasePreviewForVirtualCleanup()
    {
        CancelSettleEvaluation();

        if (_bBoxInfo != null)
            _bBoxInfo.PhysicsStack?.OnMemberRemoved(this);

        ReleaseRuntimeItem();
        _permanentlyPhysical = false;

        if (_bBoxInfo != null)
            _bBoxInfo.ClearPhysicsState();

        enabled = false;
    }

    private void ResetPoolState()
    {
        CancelSettleEvaluation();
        _permanentlyPhysical = false;
        _runtimeItem = null;
        _handOverlaps.Clear();
    }

    // Returns an active preview to the pool; safe during teardown.
    private void ReleaseRuntimeItem()
    {
        RetailItemRuntimeService service = RetailItemRuntimeService.TryGetInstance();
        if (_runtimeItem != null && service != null)
            service.ReleaseActivePhysicsPreview(_runtimeItem);
        _runtimeItem = null;
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
        yield return ShelfItemPhysicsStack.WaitForSettle(() => !IsPhysicsPreviewSleeping());

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
        return posDelta > PositionThreshold || rotDelta > RotationThresholdDegrees;
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
