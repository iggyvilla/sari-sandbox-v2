using UnityEngine;

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
    protected Rigidbody handRigidbody;
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
    private RuntimeRetailItem _rightHandItem;
    private DoorHandle _grabbedDoor;

    private bool _basketInView;
    private Vector3 _basketStoredPosition;
    private Quaternion _basketStoredRotation;
    private Transform _basketStoredParent;

    private bool _hasPendingAgentPosition;
    private Vector3 _pendingAgentPosition;
    private bool _hasPendingAgentRotation;
    private Quaternion _pendingAgentRotation;
    private bool _resetAgentVelocity;

    // The hand's pose is always described relative to its parent (the body). Every
    // FixedUpdate the hand Rigidbody is re-driven to this target, so it stays attached
    // to the body and pushes items/doors with a finite swept velocity.
    private Vector3 _handTargetLocalPosition;
    private Quaternion _handTargetLocalRotation;

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
        handRigidbody = agentHand.GetComponent<Rigidbody>();
        SetHandPoseTargetFromTransform();
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
        ApplyPendingAgentPose();
        // Drive the hand AFTER the body pose is applied this step, so it reads the
        // body's current transform and never lags a frame behind it.
        DriveHandToTarget();
        if (isMultiplayerAgent) return;

        RaycastHit hit;
        Debug.DrawRay(transform.position, transform.TransformDirection(Vector3.forward) * 10f, Color.yellow);

        if (DataHandler.Instance.agentInteractionStyle == AgentInteractionStyle.Manual) return;

        if (Input.GetKey(KeyCode.Q) && _rightHandItem?.gameObject != null)
        {
            ThrowItem();
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

                if (_rightHandItem == null)
                {
                    Vector3 handLocation = transform.position
                                           + transform.forward * 0.2f
                                           + transform.right * 0.1f
                                           + transform.up * -0.1f;

                    ItemBBoxInfo itemBBoxInfo = hit.collider.GetComponent<ItemBBoxInfo>();
                    _rightHandItem = RetailItemRuntimeService.Instance.PickUpFromBBox(
                        itemBBoxInfo,
                        transform,
                        handLocation,
                        transform.rotation,
                        new Vector3(0f, -60f, 0f)
                    );
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

                if (Input.GetKey(KeyCode.RightArrow)) RotateAgent(Vector3.up, r);
                else if (Input.GetKey(KeyCode.LeftArrow)) RotateAgent(Vector3.up, -r);
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

        if (transform == rigidbody.transform)
        {
            Vector3 e = GetPendingAgentRotation().eulerAngles;
            e.z = 0;
            QueueAgentRotation(Quaternion.Euler(e));
        }
        else
        {
            Vector3 e = transform.eulerAngles;
            e.z = 0;
            transform.rotation = Quaternion.Euler(e);
        }
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
            MoveHandToWorldPose(_grabbedDoor.transform.position, agentHand.transform.rotation);
            DriveDoorFromInput();
            return;
        }

        float speed = handMoveSpeed * Time.fixedDeltaTime;
        Vector3 localPos = _handTargetLocalPosition;

        if (Input.GetKey(KeyCode.E)) localPos += Vector3.up * speed;
        if (Input.GetKey(KeyCode.Q)) localPos -= Vector3.up * speed;
        if (Input.GetKey(KeyCode.W)) localPos += Vector3.forward * speed;
        if (Input.GetKey(KeyCode.S)) localPos -= Vector3.forward * speed;
        if (Input.GetKey(KeyCode.A)) localPos -= Vector3.right * speed;
        if (Input.GetKey(KeyCode.D)) localPos += Vector3.right * speed;

        // if (localPos.magnitude > handMoveRange)
        //     localPos = localPos.normalized * handMoveRange;

        MoveHandToLocalPose(localPos, _handTargetLocalRotation);
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
        QueueAgentPose(worldPosition, Quaternion.Euler(eulerRotation), resetVelocity: true);
    }

    public void TranslateAgent(Vector3 deltaTranslation, Vector3 deltaRotation)
    {
        Vector3 position = GetPendingAgentPosition() + deltaTranslation;
        Vector3 euler = GetPendingAgentRotation().eulerAngles + deltaRotation;
        euler.z = 0;
        QueueAgentPose(position, Quaternion.Euler(euler), resetVelocity: true);
    }

    public void TransformHand(Vector3 localPosition, Vector3 eulerRotation)
    {
        if (agentHand == null) return;
        if (localPosition.magnitude > handMoveRange) return;
        MoveHandToWorldPose(
            transform.TransformPoint(localPosition),
            transform.rotation * Quaternion.Euler(eulerRotation));
    }

    public void TranslateHand(Vector3 deltaLocalPosition, Vector3 deltaRotation)
    {
        if (agentHand == null) return;
        Vector3 localPos = _handTargetLocalPosition + deltaLocalPosition;
        if (localPos.magnitude > handMoveRange)
            localPos = localPos.normalized * handMoveRange;
        MoveHandToLocalPose(localPos, _handTargetLocalRotation * Quaternion.Euler(deltaRotation));
    }

    public void ResetHandPosition()
    {
        if (agentHand == null) return;
        MoveHandToLocalPose(_initialHandLocalPosition, _initialHandLocalRotation);
    }

    public bool IsHoldingItem() => _rightHandItem?.gameObject != null;

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
                Physics.IgnoreLayerCollision(9, 12, true);
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
            // If we're currently grabbing a door, un-grab it
            if (_grabbedDoor != null)
            {
                MoveHandToLocalPose(_initialHandLocalPosition, _initialHandLocalRotation);
                _grabbedDoor.DoorRigidbody.linearVelocity = Vector3.zero;
                _grabbedDoor.DoorRigidbody.angularVelocity = Vector3.zero;
                _grabbedDoor = null;
                Physics.IgnoreLayerCollision(9, 12, false);
            }
            isGripped = false;
        }

        if (!isGripped && _rightHandItem?.gameObject != null)
            DropCurrentlyHeldItem();
    }

    private void DropCurrentlyHeldItem()
    {
        RetailItemRuntimeService.Instance.DropHeldItem(_rightHandItem, _itemBBoxMaterial);
        _rightHandItem = null;
    }

    private void InstantiateItemFromBBox()
    {
        ItemBBoxInfo itemBBoxInfo = _handCollisionDetector.DetectedItemBBoxInfo;
        _rightHandItem = RetailItemRuntimeService.Instance.PickUpFromBBox(
            itemBBoxInfo,
            agentHand.transform,
            agentHand.transform.position - new Vector3(0, 0.1f, 0),
            transform.rotation,
            new Vector3(0f, -60f, 0f)
        );
    }

    private void ThrowItem()
    {
        RetailItemRuntimeService.Instance.ThrowHeldItem(
            _rightHandItem,
            _itemBBoxMaterial,
            transform.forward * throwStrength);
        _rightHandItem = null;
    }

    private Vector3 GetPendingAgentPosition()
    {
        return _hasPendingAgentPosition ? _pendingAgentPosition : rigidbody.position;
    }

    private Quaternion GetPendingAgentRotation()
    {
        return _hasPendingAgentRotation ? _pendingAgentRotation : rigidbody.rotation;
    }

    private void QueueAgentPose(Vector3 position, Quaternion rotation, bool resetVelocity)
    {
        _pendingAgentPosition = position;
        _hasPendingAgentPosition = true;
        _pendingAgentRotation = NormalizeRotation(rotation, rigidbody.rotation);
        _hasPendingAgentRotation = true;
        _resetAgentVelocity |= resetVelocity;
    }

    private void QueueAgentRotation(Quaternion rotation)
    {
        _pendingAgentRotation = NormalizeRotation(rotation, rigidbody.rotation);
        _hasPendingAgentRotation = true;
    }

    private void RotateAgent(Vector3 axis, float degrees)
    {
        QueueAgentRotation(Quaternion.AngleAxis(degrees, axis) * GetPendingAgentRotation());
    }

    private void ApplyPendingAgentPose()
    {
        if (rigidbody == null) return;

        if (_resetAgentVelocity)
        {
            rigidbody.linearVelocity = Vector3.zero;
            rigidbody.angularVelocity = Vector3.zero;
        }

        if (_hasPendingAgentPosition)
            rigidbody.MovePosition(_pendingAgentPosition);
        if (_hasPendingAgentRotation)
            rigidbody.MoveRotation(_pendingAgentRotation);

        _hasPendingAgentPosition = false;
        _hasPendingAgentRotation = false;
        _resetAgentVelocity = false;
    }

    private void SetHandPoseTargetFromTransform()
    {
        _handTargetLocalPosition = agentHand.transform.localPosition;
        _handTargetLocalRotation = NormalizeRotation(agentHand.transform.localRotation, Quaternion.identity);
    }

    // The Move* methods only record where the hand should sit relative to the body.
    // The actual move happens in DriveHandToTarget every FixedUpdate.
    private void MoveHandToLocalPose(Vector3 localPosition, Quaternion localRotation)
    {
        _handTargetLocalPosition = localPosition;
        _handTargetLocalRotation = NormalizeRotation(localRotation, _handTargetLocalRotation);
    }

    private void MoveHandToWorldPose(Vector3 worldPosition, Quaternion worldRotation)
    {
        Transform parent = agentHand.transform.parent;
        worldRotation = NormalizeRotation(worldRotation, agentHand.transform.rotation);
        _handTargetLocalPosition = parent != null ? parent.InverseTransformPoint(worldPosition) : worldPosition;
        _handTargetLocalRotation = NormalizeRotation(
            parent != null ? Quaternion.Inverse(parent.rotation) * worldRotation : worldRotation,
            _handTargetLocalRotation);
    }

    // Re-pin the hand to its parent-relative target every physics step. Because this
    // reads the parent's live transform, the hand stays rigidly attached to the body
    // through any body motion, while MovePosition still sweeps it for collisions so it
    // can push items and doors instead of teleporting through them.
    private void DriveHandToTarget()
    {
        if (agentHand == null) return;

        Transform parent = agentHand.transform.parent;
        Vector3 worldPosition = parent != null
            ? parent.TransformPoint(_handTargetLocalPosition)
            : _handTargetLocalPosition;
        Quaternion worldRotation = NormalizeRotation(
            parent != null ? parent.rotation * _handTargetLocalRotation : _handTargetLocalRotation,
            agentHand.transform.rotation);

        // IK targets have no Rigidbody and intentionally keep their transform-based behavior.
        if (handRigidbody == null)
        {
            agentHand.transform.SetPositionAndRotation(worldPosition, worldRotation);
            return;
        }

        handRigidbody.MovePosition(worldPosition);
        handRigidbody.MoveRotation(worldRotation);
    }

    private static Quaternion NormalizeRotation(Quaternion rotation, Quaternion fallback)
    {
        float sqrMagnitude =
            rotation.x * rotation.x +
            rotation.y * rotation.y +
            rotation.z * rotation.z +
            rotation.w * rotation.w;

        if (float.IsNaN(sqrMagnitude) || float.IsInfinity(sqrMagnitude) || sqrMagnitude < 0.000001f)
        {
            rotation = fallback;
            sqrMagnitude =
                rotation.x * rotation.x +
                rotation.y * rotation.y +
                rotation.z * rotation.z +
                rotation.w * rotation.w;

            if (float.IsNaN(sqrMagnitude) || float.IsInfinity(sqrMagnitude) || sqrMagnitude < 0.000001f)
                return Quaternion.identity;
        }

        float inverseMagnitude = 1f / Mathf.Sqrt(sqrMagnitude);
        return new Quaternion(
            rotation.x * inverseMagnitude,
            rotation.y * inverseMagnitude,
            rotation.z * inverseMagnitude,
            rotation.w * inverseMagnitude);
    }
}
