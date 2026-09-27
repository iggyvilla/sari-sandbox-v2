using UnityEngine;

public class HandCollisionDetector : MonoBehaviour
{
    private const int MaxGrabCandidates = 32;

    public ItemBBoxInfo DetectedItemBBoxInfo { get; private set; }
    public GameObject DetectedItem { get; private set; }
    public DoorHandle DetectedDoorHandle { get; private set; }

    [SerializeField] private float grabRadius = 0.06f;
    [SerializeField] private float grabForwardBias = 0.05f;
    [SerializeField] private float grabUpBias = 0f;
    [SerializeField] private float grabLeftBias = 0f;

    private readonly Collider[] _grabCandidates = new Collider[MaxGrabCandidates];
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

        // Trigger exits aren't delivered while disabled, so drop overlap state.
        _shelfDoorOverlapCount = 0;
        DetectedDoorHandle = null;
        ClearDetectedItem();
    }

    private void Update()
    {
        UpdateNearestItem();

        if (_itemOutlineController != null) _itemOutlineController.OnGaze();
        if (DetectedDoorHandle != null && DetectedDoorHandle.OutlineController != null)
            DetectedDoorHandle.OutlineController.OnGaze();
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

        int hitCount = Physics.OverlapSphereNonAlloc(
            grabCenter,
            grabRadius,
            _grabCandidates,
            _itemBBoxMask,
            QueryTriggerInteraction.Collide);

        Collider nearest = null;
        ItemBBoxInfo nearestInfo = null;
        float nearestDist = float.MaxValue;
        bool nearestIsPhysics = false;

        for (int i = 0; i < hitCount; i++)
        {
            Collider hit = _grabCandidates[i];
            float dist = Vector3.Distance(grabCenter, hit.ClosestPoint(grabCenter));
            ItemBBoxInfo info = hit.GetComponentInParent<ItemBBoxInfo>();
            bool isPhysics = info != null && info.isPhysicsObject;

            // Physics-backed items always win over shelf items.
            if ((isPhysics && !nearestIsPhysics) || (isPhysics == nearestIsPhysics && dist < nearestDist))
            {
                nearest = hit;
                nearestInfo = info;
                nearestDist = dist;
                nearestIsPhysics = isPhysics;
            }
        }

        if (nearest == null)
        {
            ClearDetectedItem();
            return;
        }

        if (nearest.gameObject == DetectedItem) return;

        DetectedItem = nearest.gameObject;
        DetectedItemBBoxInfo = nearestInfo;
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
