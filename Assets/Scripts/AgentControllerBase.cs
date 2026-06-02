using DG.Tweening;
using TMPro;
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

    [Header("Chat")]
    [SerializeField] private TextMeshPro overheadChatText;

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
    private RuntimeRetailItem _rightHandItem;
    private DoorHandle _grabbedDoor;
    private Rigidbody _handRigidbody;
    private Vector3 _desiredHandLocalPosition;
    private Quaternion _desiredHandLocalRotation;
    private bool _hasDesiredHandPose;
    private bool _hasPendingHandPose;

    // Body translation requested this physics step via MovePosition (a deferred move that
    // hasn't been applied to the transform yet). ApplyDesiredHandPose adds this so the hand
    // is pinned to where the body WILL be, not where it currently is.
    private Vector3 _pendingBodyTranslation;

    private bool _basketInView;
    private Vector3 _basketStoredPosition;
    private Quaternion _basketStoredRotation;
    private Transform _basketStoredParent;
    private Sequence _overheadChatSequence;

    protected virtual void Start()
    {
        rigidbody = GetComponentInParent<Rigidbody>() ?? GetComponent<Rigidbody>() ?? GetComponentInChildren<Rigidbody>();
        interactableLayerMask = LayerMask.GetMask("SariInteractable");
        InitializeHandComponents();
    }

    protected virtual void OnDestroy()
    {
        _overheadChatSequence?.Kill();
    }

    public void ShowChat(string chatText)
    {
        if (string.IsNullOrEmpty(chatText) || overheadChatText == null) return;

        _overheadChatSequence?.Kill();
        overheadChatText.text = chatText;
        overheadChatText.alpha = 1f;

        _overheadChatSequence = DOTween.Sequence()
            .AppendInterval(4f)
            .Append(DOTween.To(
                () => overheadChatText.alpha,
                alpha => overheadChatText.alpha = alpha,
                0f,
                1f));
    }

    protected virtual void InitializeHandComponents()
    {
        if (agentHand == null) return;
        handAnimator = agentHand.GetComponentInChildren<Animator>();
        _handCollisionDetector = agentHand.GetComponent<HandCollisionDetector>();
        _initialHandLocalPosition = agentHand.transform.localPosition;
        _initialHandLocalRotation = agentHand.transform.localRotation;
        _handRigidbody = agentHand.GetComponent<Rigidbody>();
        _desiredHandLocalPosition = _initialHandLocalPosition;
        _desiredHandLocalRotation = _initialHandLocalRotation;
        _hasDesiredHandPose = _handRigidbody != null;
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
        _pendingBodyTranslation = Vector3.zero;
        UpdateHandControlMode();
        HandleMovement();
        ApplyDesiredHandPose();
        if (isMultiplayerAgent) return;

        Debug.DrawRay(transform.position, transform.TransformDirection(Vector3.forward) * 10f, Color.yellow);

        if (DataHandler.Instance.agentInteractionStyle == AgentInteractionStyle.Manual) return;
        
        // Gaze Mode 

        if (Input.GetKey(KeyCode.Q) && _rightHandItem?.gameObject != null)
        {
            ThrowItem();
        }
        
        RaycastHit hit;
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
                if (Input.GetKey(KeyCode.W)) TranslateAgent(fwd * m, Vector3.zero);
                else if (Input.GetKey(KeyCode.A)) TranslateAgent(-right * m, Vector3.zero);
                else if (Input.GetKey(KeyCode.S)) TranslateAgent(-fwd * m, Vector3.zero);
                else if (Input.GetKey(KeyCode.D)) TranslateAgent(right * m, Vector3.zero);

                if (Input.GetKey(KeyCode.I)) ResetHandPosition();

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

    private void UpdateHandControlMode()
    {
        if (_handRigidbody == null) return;

        bool shouldBeKinematic = !IsManualHandControlActive();
        if (_handRigidbody.isKinematic == shouldBeKinematic) return;

        if (shouldBeKinematic)
        {
            _handRigidbody.linearVelocity = Vector3.zero;
            _handRigidbody.angularVelocity = Vector3.zero;
            _handRigidbody.isKinematic = true;
        }
        else
        {
            // Start manual control from the live pose in case tracking drove the kinematic hand.
            _desiredHandLocalPosition = agentHand.transform.localPosition;
            _desiredHandLocalRotation = agentHand.transform.localRotation;
            _hasDesiredHandPose = true;
            _handRigidbody.isKinematic = false;
            _handRigidbody.linearVelocity = Vector3.zero;
            _handRigidbody.angularVelocity = Vector3.zero;
        }

        _hasPendingHandPose = true;
    }

    private bool IsManualHandControlActive()
    {
        return !isMultiplayerAgent
               && DataHandler.Instance != null
               && DataHandler.Instance.agentInteractionStyle == AgentInteractionStyle.Manual
               && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl));
    }

    private void ApplyDesiredHandPose()
    {
        if (_handRigidbody == null || !_hasDesiredHandPose) return;
        // Re-pin the hand to its parent-local pose every step (even while kinematic) so a
        // nested Rigidbody isn't left behind when the body moves. The body's translation is
        // applied via a deferred MovePosition, so add _pendingBodyTranslation to target where
        // the body WILL be this step; rotation is written to the transform immediately, so
        // handParent.rotation is already current and needs no prediction.

        Transform handParent = agentHand.transform.parent;
        Vector3 worldPosition = handParent != null
            ? handParent.TransformPoint(_desiredHandLocalPosition) + _pendingBodyTranslation
            : _desiredHandLocalPosition + _pendingBodyTranslation;
        Quaternion worldRotation = handParent != null
            ? handParent.rotation * _desiredHandLocalRotation
            : _desiredHandLocalRotation;

        _handRigidbody.MovePosition(worldPosition);
        _handRigidbody.MoveRotation(worldRotation);
        _hasPendingHandPose = false;
    }

    private Vector3 GetHandLocalPosition()
    {
        return _handRigidbody != null && _hasDesiredHandPose
            ? _desiredHandLocalPosition
            : agentHand.transform.localPosition;
    }

    private Quaternion GetHandLocalRotation()
    {
        return _handRigidbody != null && _hasDesiredHandPose
            ? _desiredHandLocalRotation
            : agentHand.transform.localRotation;
    }

    private void SetHandLocalPose(Vector3 localPosition, Quaternion localRotation)
    {
        if (_handRigidbody == null)
        {
            agentHand.transform.localPosition = localPosition;
            agentHand.transform.localRotation = localRotation;
            return;
        }

        _desiredHandLocalPosition = localPosition;
        _desiredHandLocalRotation = localRotation;
        _hasDesiredHandPose = true;
        _hasPendingHandPose = true;
    }

    private void SetHandWorldPosition(Vector3 worldPosition)
    {
        if (_handRigidbody == null)
        {
            agentHand.transform.position = worldPosition;
            return;
        }

        Transform handParent = agentHand.transform.parent;
        _desiredHandLocalPosition = handParent != null
            ? handParent.InverseTransformPoint(worldPosition)
            : worldPosition;
        _hasDesiredHandPose = true;
        _hasPendingHandPose = true;
    }

    private void SetHandWorldPose(Vector3 worldPosition, Quaternion worldRotation)
    {
        if (_handRigidbody == null)
        {
            agentHand.transform.position = worldPosition;
            agentHand.transform.rotation = worldRotation;
            return;
        }

        Transform handParent = agentHand.transform.parent;
        _desiredHandLocalPosition = handParent != null
            ? handParent.InverseTransformPoint(worldPosition)
            : worldPosition;
        _desiredHandLocalRotation = handParent != null
            ? Quaternion.Inverse(handParent.rotation) * worldRotation
            : worldRotation;
        _hasDesiredHandPose = true;
        _hasPendingHandPose = true;
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
            SetHandWorldPosition(_grabbedDoor.transform.position);
            DriveDoorFromInput();
            return;
        }

        float speed = handMoveSpeed * Time.fixedDeltaTime;
        Vector3 localPos = GetHandLocalPosition();

        if (Input.GetKey(KeyCode.E)) localPos += Vector3.up * speed;
        if (Input.GetKey(KeyCode.Q)) localPos -= Vector3.up * speed;
        if (Input.GetKey(KeyCode.W)) localPos += Vector3.forward * speed;
        if (Input.GetKey(KeyCode.S)) localPos -= Vector3.forward * speed;
        if (Input.GetKey(KeyCode.A)) localPos -= Vector3.right * speed;
        if (Input.GetKey(KeyCode.D)) localPos += Vector3.right * speed;

        // if (localPos.magnitude > handMoveRange)
        //     localPos = localPos.normalized * handMoveRange;

        SetHandLocalPose(localPos, GetHandLocalRotation());
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
        // MovePosition (instead of writing transform.position) so the body sweeps against
        // colliders. The move is deferred to the physics step, so record the accumulated
        // delta for ApplyDesiredHandPose to keep the hand in sync this same step.
        _pendingBodyTranslation += deltaTranslation;
        rigidbody.MovePosition(rigidbody.position + _pendingBodyTranslation);
        Vector3 euler = transform.eulerAngles + deltaRotation;
        euler.z = 0;
        transform.rotation = Quaternion.Euler(euler);
    }

    public void TransformHand(Vector3 localPosition, Vector3 eulerRotation)
    {
        if (agentHand == null) return;
        if (localPosition.magnitude > handMoveRange) return;
        SetHandWorldPose(
            transform.TransformPoint(localPosition),
            transform.rotation * Quaternion.Euler(eulerRotation));
    }

    public void TranslateHand(Vector3 deltaLocalPosition, Vector3 deltaRotation)
    {
        if (agentHand == null) return;
        Vector3 localPos = GetHandLocalPosition() + deltaLocalPosition;
        if (localPos.magnitude > handMoveRange)
            localPos = localPos.normalized * handMoveRange;
        Quaternion localRotation = GetHandLocalRotation() * Quaternion.Euler(deltaRotation);
        SetHandLocalPose(localPos, localRotation);
    }

    public void ResetHandPosition()
    {
        if (agentHand == null) return;
        SetHandLocalPose(_initialHandLocalPosition, _initialHandLocalRotation);
    }

    public Transform MovementRoot =>
        rigidbody != null
            ? rigidbody.transform
            : GetComponentInParent<Rigidbody>()?.transform ?? transform;

    public Transform ViewTransform => transform;

    public Transform HandTransform => agentHand != null ? agentHand.transform : null;

    public float GripAmount => currentGrip;

    public float TriggerAmount => currentTrigger;

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
                ResetHandPosition();
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
}
