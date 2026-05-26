using UnityEngine;

public class HandCollisionDetector : MonoBehaviour
{
    public ItemBBoxInfo DetectedItemBBoxInfo { get; private set; }
    public GameObject DetectedItem { get; private set; }
    public DoorHandle DetectedDoorHandle { get; private set; }
    public bool IsPointing { get; set; }

    [SerializeField] private float grabRadius = 0.02f;

    private OutlineController _itemOutlineController;
    private int _shelfDoorOverlapCount;
    private LayerMask _itemBBoxMask;

    private void Awake()
    {
        _itemBBoxMask = LayerMask.GetMask("ItemBBox");
    }

    private void Update()
    {
        UpdateNearestItem();

        if (_itemOutlineController != null) _itemOutlineController.OnGaze();
        if (DetectedDoorHandle != null) DetectedDoorHandle.OutlineController.OnGaze();
    }

    private void UpdateNearestItem()
    {
        if (_shelfDoorOverlapCount > 0)
        {
            ClearDetectedItem();
            return;
        }

        Collider[] hits = Physics.OverlapSphere(
            transform.position,
            grabRadius,
            _itemBBoxMask,
            QueryTriggerInteraction.Collide);

        Collider nearest = null;
        float nearestDist = float.MaxValue;

        foreach (Collider hit in hits)
        {
            float dist = Vector3.Distance(
                transform.position,
                hit.ClosestPoint(transform.position));

            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearest = hit;
            }
        }

        if (nearest == null)
        {
            ClearDetectedItem();
            return;
        }

        if (nearest.gameObject == DetectedItem) return;

        ClearDetectedItem();
        DetectedItem = nearest.gameObject;
        DetectedItemBBoxInfo = nearest.GetComponentInParent<ItemBBoxInfo>();
        _itemOutlineController = DetectedItem.GetComponentInChildren<OutlineController>();
    }

    private void ClearDetectedItem()
    {
        DetectedItem = null;
        DetectedItemBBoxInfo = null;
        _itemOutlineController = null;
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, grabRadius);
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("ShelfDoor")) { _shelfDoorOverlapCount++; return; }

        SimpleVRButton button = other.GetComponent<SimpleVRButton>();
        if (button != null) { button.Tapped(); return; }

        DoorHandle dh = other.GetComponent<DoorHandle>();
        if (dh != null) DetectedDoorHandle = dh;
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.CompareTag("ShelfDoor"))
        {
            _shelfDoorOverlapCount = Mathf.Max(0, _shelfDoorOverlapCount - 1);
            return;
        }

        DoorHandle dh = other.GetComponent<DoorHandle>();
        if (dh != null && dh == DetectedDoorHandle) DetectedDoorHandle = null;
    }
}
