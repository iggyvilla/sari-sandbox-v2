using System.Collections;
using UnityEngine;

// Attached to each shelf ItemBBox trigger alongside ItemBBoxInfo.
// When the agent's hand sphere (or a physics item) enters, swaps the GPU-instanced
// mesh for a real physics prefab. On exit, either returns the prefab to the pool
// (if it barely moved) or keeps it permanently as a grabbable dropped item.
[RequireComponent(typeof(ItemBBoxInfo))]
public class ItemPhysicsProxy : MonoBehaviour
{
    // How far (meters) or how many degrees the item must move from spawn
    // before we consider it "disturbed" and keep it permanently.
    [SerializeField] private float positionThreshold = 0.05f;
    [SerializeField] private float rotationThreshold = 5f;

    private ItemBBoxInfo _bBoxInfo;
    private bool _physicsActive;
    private bool _permanentlyPhysical;
    private GameObject _physicsObj;
    private Rigidbody _physicsRb;
    private Vector3 _spawnedPosition;
    private Quaternion _spawnedRotation;
    private Coroutine _settleCoroutine;

    void Awake()
    {
        _bBoxInfo = GetComponent<ItemBBoxInfo>();
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
            ItemPoolingManager.Instance?.ReturnToPool(_bBoxInfo.itemId, _physicsObj);
    }

    private void ActivatePhysics()
    {
        BatchInstancer bi = GPUInstanceTracker.Instance?.GetBatchInstancerFromId(_bBoxInfo.itemId);
        bi?.RemoveSingleDrawData(_bBoxInfo.instanceLODData);

        Vector3 pos = _bBoxInfo.instanceLODData.position0;
        Quaternion rot = _bBoxInfo.spawnRotation;

        _physicsObj = ItemPoolingManager.Instance.GetOrCreate(_bBoxInfo.itemId, pos, rot);
        _physicsRb = _physicsObj != null ? _physicsObj.GetComponent<Rigidbody>() : null;
        
        _spawnedPosition = pos;
        _spawnedRotation = rot;
        _physicsActive = true;
    }

    // Waits for the physics object to come to rest, then decides whether to
    // return it to the pool (barely moved) or keep it as a permanent dropped item.
    private IEnumerator WaitAndEvaluate()
    {
        // Give physics at least one frame to start
        yield return null;

        // Wait until the rigidbody sleeps or the object is gone (e.g. grabbed)
        while (_physicsRb != null && !_physicsRb.IsSleeping())
            yield return new WaitForSeconds(0.1f);

        if (_physicsObj == null)
        {
            // Object was grabbed and destroyed — GPU instance is already gone, nothing to restore
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
            ItemPoolingManager.Instance?.ReturnToPool(_bBoxInfo.itemId, _physicsObj);
            RestoreGPUInstance();
        }

        _physicsActive = false;
        _physicsObj = null;
        _physicsRb = null;
        _settleCoroutine = null;
    }

    private void RestoreGPUInstance()
    {
        GameObject prefab = Resources.Load<GameObject>("Prefabs/Products/" + _bBoxInfo.itemId);
        if (prefab != null)
            GPUInstanceTracker.Instance?.AddToInstance(_bBoxInfo.itemId, prefab, _bBoxInfo.instanceLODData);
    }
}
