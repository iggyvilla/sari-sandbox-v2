using Unity.VisualScripting;
using UnityEngine;

public struct RightHandItem
{
    public GameObject obj;
    public string itemId;

    public RightHandItem(GameObject obj, string itemId)
    {
        this.obj = obj;
        this.itemId = itemId;
    }
}

public abstract class AgentControllerBase : MonoBehaviour
{
    public bool isMultiplayerAgent = false;

    [Header("Agent Properties")]
    [SerializeField] protected float movementSpeed;
    [SerializeField] protected float rotateSpeed;
    [SerializeField] protected float throwStrength;

    [Header("Agent Hand Object")]
    [SerializeField] protected GameObject agentHand;

    [Header("Basket")]
    public GameObject agentBasket;
    public Vector3 basketOffset = new Vector3(0f, -0.3f, 0.6f);

    [Header("Item Drop Settings")]
    [SerializeField] private Material _itemBBoxMaterial;

    [Header("Item Physics")]
    [SerializeField] private float physicsActivationRadius = 0.4f;

    [Header("Manual Hand Control")]
    public float handMoveRange = 0.5f;
    public float handMoveSpeed = 1f;
    public float gripSpeed = 2f;
    public float doorHandleForce = 5f;

    protected Rigidbody rigidbody;
    protected Animator handAnimator;
    protected HandCollisionDetector _handCollisionDetector;
    protected BoxCollider _handCollider;
    protected Vector3 _defaultColliderSize;
    protected Vector3 _defaultColliderCenter;
    protected Vector3 _initialHandLocalPosition;
    protected Quaternion _initialHandLocalRotation;
    protected bool isGripped;
    protected bool isPointing;
    protected float currentGrip;
    protected float currentTrigger;

    private LayerMask interactableLayerMask;
    private RightHandItem _rightHandItem;
    private DoorHandle _grabbedDoor;

    private bool _basketInView;
    private Vector3 _basketStoredPosition;
    private Quaternion _basketStoredRotation;
    private Transform _basketStoredParent;

    protected virtual void Start()
    {
        rigidbody = GetComponentInParent<Rigidbody>() ?? GetComponent<Rigidbody>() ?? GetComponentInChildren<Rigidbody>();
        interactableLayerMask = LayerMask.GetMask("SariInteractable");
        InitializeHandComponents();
    }

    protected virtual void InitializeHandComponents()
    {
        if (agentHand == null) return;
        handAnimator = agentHand.GetComponentInChildren<Animator>();
        _handCollisionDetector = agentHand.GetComponent<HandCollisionDetector>();
        _initialHandLocalPosition = agentHand.transform.localPosition;
        _initialHandLocalRotation = agentHand.transform.localRotation;
        _handCollider = agentHand.GetComponent<BoxCollider>();
        if (_handCollider != null)
        {
            _defaultColliderSize = _handCollider.size;
            _defaultColliderCenter = _handCollider.center;
        }

        SetupPhysicsActivationSphere();
    }

    private void SetupPhysicsActivationSphere()
    {
        GameObject sphereObj = new GameObject("PhysicsActivationSphere");
        sphereObj.transform.SetParent(agentHand.transform, worldPositionStays: false);
        sphereObj.layer = LayerMask.NameToLayer("PhysicsActivator");
        
        Rigidbody rb =  sphereObj.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        
        SphereCollider sc = sphereObj.AddComponent<SphereCollider>();
        sc.isTrigger = true;
        sc.radius = physicsActivationRadius;
        sphereObj.AddComponent<HandPhysicsSphere>();
    }

    void FixedUpdate()
    {
        HandleMovement();
        if (isMultiplayerAgent) return;

        RaycastHit hit;
        Debug.DrawRay(transform.position, transform.TransformDirection(Vector3.forward) * 10f, Color.yellow);

        if (DataHandler.Instance.agentInteractionStyle == AgentInteractionStyle.Manual) return;

        if (Input.GetKey(KeyCode.Q) && _rightHandItem.obj != null)
        {
            ThrowItem(_rightHandItem.obj);
        }

        if (Physics.Raycast(
                transform.position,
                transform.TransformDirection(Vector3.forward),
                out hit,
                Mathf.Infinity,
                interactableLayerMask))
        {
            if (hit.collider.CompareTag("Wall")) return;

            string hitName = hit.transform.name;
            SariUIHandler.Instance.UpdateInfoText(hitName);

            OutlineController outlineControllerScript = hit.collider.GetComponent<OutlineController>();
            if (outlineControllerScript) outlineControllerScript.OnGaze();

            if (Input.GetKey(KeyCode.Return))
            {
                HingedDoorBuilder hingedDoorHandler = hit.collider.GetComponentInParent<HingedDoorBuilder>();
                if (hingedDoorHandler != null)
                {
                    hingedDoorHandler.ToggleDoor();
                    return;
                }

                if (_rightHandItem.obj == null)
                {
                    var selectedItem = Resources.Load<GameObject>("Prefabs/Products/" + hitName);
                    selectedItem.transform.position = Vector3.zero;

                    Vector3 handLocation = transform.position
                                           + transform.forward * 0.2f
                                           + transform.right * 0.1f
                                           + transform.up * -0.1f;

                    ItemBBoxInfo itemBBoxInfo = hit.collider.GetComponent<ItemBBoxInfo>();
                    itemBBoxInfo.DeleteItem();

                    DisablePhysics(selectedItem);

                    selectedItem = Instantiate(selectedItem, handLocation, transform.rotation, transform);
                    selectedItem.transform.Rotate(Vector3.up, -60);
                    selectedItem.tag = "RetailItem";

                    _rightHandItem = new RightHandItem(selectedItem, hitName);
                }
            }
        }
    }

    void Update()
    {
        if (!isMultiplayerAgent && DataHandler.Instance.agentInteractionStyle == AgentInteractionStyle.Manual)
        {
            if (Input.GetKeyDown(KeyCode.Return)) ToggleGrip();
            if (Input.GetKeyDown(KeyCode.P)) TogglePoint();
            if (Input.GetKeyDown(KeyCode.X)) ToggleBasketInView();
        }
    }

    private void HandleMovement()
    {
        if (!isMultiplayerAgent)
        {
            Vector3 fwd = transform.forward;
            Vector3 right = transform.right;
            float m = movementSpeed * Time.deltaTime;
            float r = rotateSpeed * Time.deltaTime;
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

            if (!ctrl)
            {
                if (Input.GetKey(KeyCode.W)) rigidbody.AddForce(fwd * m, ForceMode.Impulse);
                else if (Input.GetKey(KeyCode.A)) rigidbody.AddForce(-right * m, ForceMode.Impulse);
                else if (Input.GetKey(KeyCode.S)) rigidbody.AddForce(-fwd * m, ForceMode.Impulse);
                else if (Input.GetKey(KeyCode.D)) rigidbody.AddForce(right * m, ForceMode.Impulse);

                if (Input.GetKey(KeyCode.RightArrow)) transform.Rotate(Vector3.up, r);
                else if (Input.GetKey(KeyCode.LeftArrow)) transform.Rotate(Vector3.up, -r);
                else ApplyVerticalRotation(r);
            }
            else
            {
                if (DataHandler.Instance.agentInteractionStyle == AgentInteractionStyle.Manual)
                    HandleManualHandControls();
            }
        }

        AnimateHand();
        AnimateBody();

        Vector3 e = transform.eulerAngles;
        e.z = 0;
        transform.rotation = Quaternion.Euler(e);
    }

    // Override in IKAgentController to route up/down into the head joint only.
    protected virtual void ApplyVerticalRotation(float r)
    {
        if (Input.GetKey(KeyCode.UpArrow)) transform.Rotate(Vector3.right, -r);
        else if (Input.GetKey(KeyCode.DownArrow)) transform.Rotate(Vector3.right, r);
    }

    // Override in IKAgentController to drive body animator parameters (speed, crouch, etc.).
    protected virtual void AnimateBody() { }

    private void AnimateHand()
    {
        if (handAnimator == null) return;

        float gripTarget = isGripped || isPointing ? 1f : 0f;
        float triggerTarget = isPointing ? 1f : 0f;
        if (isGripped) triggerTarget = 0f;

        currentGrip = Mathf.MoveTowards(currentGrip, gripTarget, gripSpeed * Time.fixedDeltaTime);
        currentTrigger = Mathf.MoveTowards(currentTrigger, triggerTarget, gripSpeed * Time.fixedDeltaTime);

        handAnimator.SetFloat("Grip", currentGrip);
        handAnimator.SetFloat("Trigger", currentTrigger);
    }

    private void HandleManualHandControls()
    {
        if (agentHand == null) return;

        if (_grabbedDoor != null)
        {
            agentHand.transform.position = _grabbedDoor.transform.position;
            DriveDoorFromInput();
            return;
        }

        float speed = handMoveSpeed * Time.fixedDeltaTime;
        Vector3 localPos = agentHand.transform.localPosition;

        if (Input.GetKey(KeyCode.E)) localPos += Vector3.up * speed;
        if (Input.GetKey(KeyCode.Q)) localPos -= Vector3.up * speed;
        if (Input.GetKey(KeyCode.W)) localPos += Vector3.forward * speed;
        if (Input.GetKey(KeyCode.S)) localPos -= Vector3.forward * speed;
        if (Input.GetKey(KeyCode.A)) localPos -= Vector3.right * speed;
        if (Input.GetKey(KeyCode.D)) localPos += Vector3.right * speed;

        // if (localPos.magnitude > handMoveRange)
        //     localPos = localPos.normalized * handMoveRange;

        agentHand.transform.localPosition = localPos;
    }

    private void DriveDoorFromInput()
    {
        Vector3 inputLocal = Vector3.zero;
        if (Input.GetKey(KeyCode.W)) inputLocal += Vector3.forward;
        if (Input.GetKey(KeyCode.S)) inputLocal -= Vector3.forward;
        if (Input.GetKey(KeyCode.A)) inputLocal -= Vector3.right;
        if (Input.GetKey(KeyCode.D)) inputLocal += Vector3.right;
        if (Input.GetKey(KeyCode.E)) inputLocal += Vector3.up;
        if (Input.GetKey(KeyCode.Q)) inputLocal -= Vector3.up;

        if (inputLocal.sqrMagnitude < 0.001f) return;

        Vector3 inputWorld = transform.TransformDirection(inputLocal.normalized);

        HingeJoint hinge = _grabbedDoor.Hinge;
        Rigidbody doorRb = _grabbedDoor.DoorRigidbody;

        Vector3 axisWorld = doorRb.transform.TransformDirection(hinge.axis);
        Vector3 anchorWorld = doorRb.transform.TransformPoint(hinge.anchor);
        Vector3 toHandle = _grabbedDoor.transform.position - anchorWorld;
        float radius = toHandle.magnitude;
        if (radius < 0.001f) return;

        Vector3 tangent = Vector3.Cross(axisWorld, toHandle.normalized);
        float tangentialAmount = Vector3.Dot(inputWorld, tangent);

        _grabbedDoor.DoorBuilder.ApplyHandForce(tangent * (tangentialAmount * doorHandleForce));
    }

    public void ToggleBasketInView()
    {
        if (agentBasket == null) return;

        if (!_basketInView)
        {
            _basketStoredPosition = agentBasket.transform.position;
            _basketStoredRotation = agentBasket.transform.rotation;
            _basketStoredParent = agentBasket.transform.parent;

            agentBasket.transform.SetParent(transform, worldPositionStays: false);
            agentBasket.transform.localPosition = basketOffset;
            agentBasket.transform.localRotation = Quaternion.identity;
        }
        else
        {
            agentBasket.transform.SetParent(_basketStoredParent, worldPositionStays: false);
            agentBasket.transform.position = _basketStoredPosition;
            agentBasket.transform.rotation = _basketStoredRotation;
        }

        _basketInView = !_basketInView;
    }

    public void TransformAgent(Vector3 worldPosition, Vector3 eulerRotation)
    {
        rigidbody.linearVelocity = Vector3.zero;
        rigidbody.angularVelocity = Vector3.zero;
        rigidbody.transform.position = worldPosition;
        transform.rotation = Quaternion.Euler(eulerRotation);
    }

    public void TranslateAgent(Vector3 deltaTranslation, Vector3 deltaRotation)
    {
        rigidbody.linearVelocity = Vector3.zero;
        rigidbody.angularVelocity = Vector3.zero;
        rigidbody.transform.position += deltaTranslation;
        Vector3 euler = transform.eulerAngles + deltaRotation;
        euler.z = 0;
        transform.rotation = Quaternion.Euler(euler);
    }

    public void TransformHand(Vector3 localPosition, Vector3 eulerRotation)
    {
        if (agentHand == null) return;
        if (localPosition.magnitude > handMoveRange) return;
        agentHand.transform.position = transform.TransformPoint(localPosition);
        agentHand.transform.rotation = transform.rotation * Quaternion.Euler(eulerRotation);
    }

    public void TranslateHand(Vector3 deltaLocalPosition, Vector3 deltaRotation)
    {
        if (agentHand == null) return;
        Vector3 localPos = agentHand.transform.localPosition + deltaLocalPosition;
        if (localPos.magnitude > handMoveRange)
            localPos = localPos.normalized * handMoveRange;
        agentHand.transform.localPosition = localPos;
        agentHand.transform.localRotation *= Quaternion.Euler(deltaRotation);
    }

    public void ResetHandPosition()
    {
        if (agentHand == null) return;
        agentHand.transform.localPosition = _initialHandLocalPosition;
        agentHand.transform.localRotation = _initialHandLocalRotation;
    }

    public bool IsHoldingItem() => _rightHandItem.obj != null;

    public void TogglePoint()
    {
        isPointing = !isPointing;
        isGripped = false;
        if (_handCollisionDetector != null) _handCollisionDetector.IsPointing = isPointing;
        if (_handCollider != null)
        {
            if (isPointing)
            {
                _handCollider.center = new Vector3(0.06f, -0.01f, 0.04f);
                Vector3 s = _handCollider.size;
                s.y = 0.02f;
                s.z = 0.13f;
                _handCollider.size = s;
            }
            else
            {
                _handCollider.center = _defaultColliderCenter;
                _handCollider.size = _defaultColliderSize;
            }
        }
    }

    public void ToggleGrip()
    {
        if (!isGripped)
        {
            // Turn off pointing mode (ensures bounding box is at palm)
            isPointing = false;
            
            if (_handCollisionDetector != null) _handCollisionDetector.IsPointing = false;
            
            // Reset our box collider to the "grip" collider
            if (_handCollider != null)
            {
                _handCollider.center = _defaultColliderCenter;
                _handCollider.size = _defaultColliderSize;
            }
            
            if (_handCollisionDetector != null && _handCollisionDetector.DetectedDoorHandle != null)
            {
                _grabbedDoor = _handCollisionDetector.DetectedDoorHandle;
            }
            else if (agentHand != null &&
                     _handCollisionDetector != null &&
                     _handCollisionDetector.DetectedItem != null &&
                     _handCollisionDetector.DetectedItemBBoxInfo != null)
            {
                InstantiateItemFromBBox();
            }
            isGripped = true;
        }
        else
        {
            if (_grabbedDoor != null)
            {
                agentHand.transform.localPosition = _initialHandLocalPosition;
                agentHand.transform.localRotation = _initialHandLocalRotation;
                _grabbedDoor.DoorRigidbody.linearVelocity = Vector3.zero;
                _grabbedDoor.DoorRigidbody.angularVelocity = Vector3.zero;
                _grabbedDoor = null;
            }
            isGripped = false;
        }

        if (!isGripped && _rightHandItem.obj != null)
            DropCurrentlyHeldItem();
    }

    private void DropCurrentlyHeldItem()
    {
        GameObject obj = _rightHandItem.obj;
        obj.transform.SetParent(null);
        Rigidbody rb = obj.GetComponent<Rigidbody>();
        rb.isKinematic = false;
        rb.useGravity = true;
        rb.interpolation = RigidbodyInterpolation.Extrapolate;
        BoxCollider boxCollider = obj.GetComponentInChildren<BoxCollider>();
        if (boxCollider != null) boxCollider.enabled = true;

        GameObject bboxObj = CreateItemBBox(obj);

        ItemBBoxInfo itemBBoxInfo = bboxObj.AddComponent<ItemBBoxInfo>();
        itemBBoxInfo.isPhysicsObject = true;
        itemBBoxInfo.itemId = _rightHandItem.itemId;

        _rightHandItem = default;
    }

    private GameObject CreateItemBBox(GameObject itemRoot)
    {
        Transform lod0 = FindLOD0(itemRoot);
        MeshFilter mf = lod0.GetComponent<MeshFilter>();
        Bounds meshBounds = mf != null ? mf.sharedMesh.bounds : new Bounds(Vector3.zero, Vector3.one);

        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.transform.SetParent(itemRoot.transform, worldPositionStays: true);
        cube.transform.position = lod0.TransformPoint(meshBounds.center);
        cube.transform.rotation = lod0.rotation;
        cube.transform.localScale = Vector3.Scale(lod0.lossyScale, meshBounds.size);

        cube.GetComponent<BoxCollider>().isTrigger = true;
        cube.GetComponent<MeshRenderer>().sharedMaterial = _itemBBoxMaterial;
        cube.tag = "RetailItemBBox";
        cube.layer = LayerMask.NameToLayer("ItemBBox");
        cube.AddComponent<OutlineFx.OutlineFx>().enabled = false;
        cube.AddComponent<OutlineController>();

        return cube;
    }

    private Transform FindLOD0(GameObject item)
    {
        if (item.transform.childCount == 0) return item.transform;
        Transform prodChild = item.transform.GetChild(0);
        foreach (Transform t in prodChild)
        {
            if (t.name.EndsWith("_LOD0"))
                return t;
        }
        return prodChild;
    }

    private void InstantiateItemFromBBox()
    {
        ItemBBoxInfo itemBBoxInfo = _handCollisionDetector.DetectedItemBBoxInfo;
        string itemName = itemBBoxInfo.itemId;

        itemBBoxInfo.DeleteItem();

        GameObject prefab = Resources.Load<GameObject>("Prefabs/Products/" + itemName);
        var spawnedItem = Instantiate(
            prefab,
            agentHand.transform.position - new Vector3(0, 0.1f, 0),
            transform.rotation,
            agentHand.transform
        );
        spawnedItem.name = itemName;
        DisablePhysics(spawnedItem);
        spawnedItem.transform.Rotate(Vector3.up, -60);
        spawnedItem.tag = "RetailItem";

        _rightHandItem = new RightHandItem(spawnedItem, itemName);
    }

    private void ThrowItem(GameObject item)
    {
        item.transform.SetParent(null);
        Rigidbody rb = item.GetComponent<Rigidbody>();
        rb.isKinematic = false;
        rb.useGravity = true;
        rb.interpolation = RigidbodyInterpolation.Extrapolate;
        rb.AddForce(transform.forward * throwStrength, ForceMode.Impulse);
        BoxCollider boxCollider = item.GetComponentInChildren<BoxCollider>();
        boxCollider.enabled = true;
        _rightHandItem = default;
    }

    private void DisablePhysics(GameObject item)
    {
        Rigidbody rb = item.GetComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.linearVelocity = Vector3.zero;
        rb.useGravity = false;
        rb.interpolation = RigidbodyInterpolation.None;
        BoxCollider boxCollider = item.GetComponentInChildren<BoxCollider>();
        boxCollider.enabled = false;

        MeshCollider[] cols = item.GetComponentsInChildren<MeshCollider>(true);
        foreach (var c in cols)
            c.isTrigger = true;
    }
}
