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
    private bool _physicsActive;
    private bool _permanentlyPhysical;
    private GameObject _physicsObj;
    private Rigidbody _physicsRb;
    private Vector3 _spawnedPosition;
    private Quaternion _spawnedRotation;
    private Vector3 _originalBBoxWorldPos;
    private Quaternion _originalBBoxWorldRot;
    private Coroutine _settleCoroutine;

    void Awake()
    {
        _bBoxInfo = GetComponent<ItemBBoxInfo>();
        _originalBBoxWorldPos = transform.position;
        _originalBBoxWorldRot = transform.rotation;
    }

    void OnTriggerEnter(Collider other)
    {
        if (!DataHandler.Instance.enableShelfItemPhysics) return;
        if (_physicsActive || _permanentlyPhysical) return;
        if (other.GetComponent<HandPhysicsSphere>() == null) return;

        ActivatePhysics();
    }

    void OnTriggerExit(Collider other)
    {
        if (other.GetComponent<HandPhysicsSphere>() == null) return;
        if (!_physicsActive || _permanentlyPhysical) return;

        _settleCoroutine = StartCoroutine(WaitAndEvaluate());
    }

    void OnDestroy()
    {
        if (_settleCoroutine != null) StopCoroutine(_settleCoroutine);

        if (_physicsActive && _physicsObj != null)
        {
            // Unparent before pooling so the BBox doesn't travel with the prefab.
            transform.SetParent(null);
            ItemPoolingManager.Instance?.ReturnToPool(_bBoxInfo.itemId, _physicsObj);
        }
    }

    private void ActivatePhysics()
    {
        BatchInstancer bi = GPUInstanceTracker.Instance?.GetBatchInstancerFromId(_bBoxInfo.itemId);
        bi?.RemoveSingleDrawData(_bBoxInfo.instanceLODData);

        Vector3 pos = _bBoxInfo.instanceLODData.position0;
        Quaternion rot = _bBoxInfo.spawnRotation;

        _physicsObj = ItemPoolingManager.Instance.GetOrCreate(_bBoxInfo.itemId, pos, rot);
        _physicsRb  = _physicsObj != null ? _physicsObj.GetComponent<Rigidbody>() : null;
        _spawnedPosition = pos;
        _spawnedRotation = rot;

        transform.SetParent(_physicsObj.transform, worldPositionStays: true);
        _bBoxInfo.isPhysicsObject    = true;
        _bBoxInfo.returnToPoolOnDelete = true;
        _bBoxInfo.onBeforeDelete     = OnBeforeItemGrabbed;

        _physicsActive = true;
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

        transform.SetParent(null);

        if (_permanentlyPhysical)
        {
            // Item had already settled at a new position and is now being picked up.
            // BBox is no longer needed as a shelf trigger — destroy it.
            Destroy(gameObject);
        }
        else
        {
            // Item was bumped but grabbed before it settled — restore BBox to shelf.
            transform.SetPositionAndRotation(_originalBBoxWorldPos, _originalBBoxWorldRot);
            // Don't call ResetBBoxToShelf() here — we need returnToPoolOnDelete to stay true
            // so DeleteItem uses ReturnToPool (immediate SetActive false) instead of Destroy
            // (deferred), which would leave the physics object visible for one frame.
            _bBoxInfo.isPhysicsObject = false;
            _bBoxInfo.onBeforeDelete  = null;
        }

        _physicsActive = false;
        _physicsObj    = null;
        _physicsRb     = null;
    }

    private IEnumerator WaitAndEvaluate()
    {
        yield return null;

        while (_physicsRb != null && !_physicsRb.IsSleeping())
            yield return new WaitForSeconds(0.1f);

        if (_physicsObj == null)
        {
            _physicsActive = false;
            _settleCoroutine = null;
            yield break;
        }

        float posDelta = Vector3.Distance(_physicsObj.transform.position, _spawnedPosition);
        float rotDelta = Quaternion.Angle(_physicsObj.transform.rotation, _spawnedRotation);

        if (posDelta > positionThreshold || rotDelta > rotationThreshold)
        {
            _permanentlyPhysical = true;
        }
        else
        {
            transform.SetParent(null);
            transform.SetPositionAndRotation(_originalBBoxWorldPos, _originalBBoxWorldRot);
            ResetBBoxToShelf();
            ItemPoolingManager.Instance?.ReturnToPool(_bBoxInfo.itemId, _physicsObj);
            RestoreGPUInstance();
        }

        _physicsActive = false;
        _physicsObj    = null;
        _physicsRb     = null;
        _settleCoroutine = null;
    }

    private void ResetBBoxToShelf()
    {
        _bBoxInfo.isPhysicsObject     = false;
        _bBoxInfo.returnToPoolOnDelete = false;
        _bBoxInfo.onBeforeDelete      = null;
    }

    private void RestoreGPUInstance()
    {
        GameObject prefab = Resources.Load<GameObject>("Prefabs/Products/" + _bBoxInfo.itemId);
        if (prefab != null)
            GPUInstanceTracker.Instance?.AddToInstance(_bBoxInfo.itemId, prefab, _bBoxInfo.instanceLODData);
    }
}
