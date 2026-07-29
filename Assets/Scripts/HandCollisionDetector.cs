using UnityEngine;

public class HandCollisionDetector : MonoBehaviour
{
    public ItemBBoxInfo DetectedItemBBoxInfo { get; private set; }
    public GameObject DetectedItem { get; private set; }
    public DoorHandle DetectedDoorHandle { get; private set; }
    public bool IsPointing { get; set; }

    [SerializeField] private float grabRadius = 0.06f;
    [SerializeField] private float grabForwardBias = 0.05f;
    [SerializeField] private float grabUpBias = 0f;
    [SerializeField] private float grabLeftBias = 0f;

    private OutlineController _itemOutlineController;
    private int _shelfDoorOverlapCount;
    private LayerMask _itemBBoxMask;

    private void Awake()
    {
        _itemBBoxMask = LayerMask.GetMask("ItemBBox");
    }

    private void OnEnable()
    {
        NearbyItemBBoxManager.Instance.RegisterActivationOrigin(transform);
    }

    private void OnDisable()
    {
        NearbyItemBBoxManager.TryGetInstance()?.UnregisterActivationOrigin(transform);
    }

    private void Update()
    {
        UpdateNearestItem();

        if (_itemOutlineController != null) _itemOutlineController.OnGaze();
        if (DetectedDoorHandle != null) DetectedDoorHandle.OutlineController.OnGaze();
    }

    private Vector3 GrabCenter =>
        transform.position
        + transform.forward * grabForwardBias
        + transform.up * grabUpBias
        + -transform.right * grabLeftBias;

    private void UpdateNearestItem()
    {
        if (_shelfDoorOverlapCount > 0)
        {
            ClearDetectedItem();
            return;
        }

        Vector3 grabCenter = GrabCenter;

        Collider[] hits = Physics.OverlapSphere(
            grabCenter,
            grabRadius,
            _itemBBoxMask,
            QueryTriggerInteraction.Collide);

        Collider nearest = null;
        float nearestDist = float.MaxValue;
        bool nearestIsPhysics = false;

        foreach (Collider hit in hits)
        {
            float dist = Vector3.Distance(grabCenter, hit.ClosestPoint(transform.position));
            bool isPhysics = hit.GetComponentInParent<ItemBBoxInfo>()?.isPhysicsObject ?? false;

            // Physics-backed items always win over shelf items.
            if (isPhysics && !nearestIsPhysics)
            {
                nearest = hit;
                nearestDist = dist;
                nearestIsPhysics = true;
            }
            else if (isPhysics == nearestIsPhysics && dist < nearestDist)
            {
                nearest = hit;
                nearestDist = dist;
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
        Gizmos.DrawWireSphere(GrabCenter, grabRadius);
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
