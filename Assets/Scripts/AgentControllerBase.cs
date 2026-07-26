using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;

public enum AgentHandSide
{
    Left,
    Right
}

public abstract class AgentControllerBase : MonoBehaviour
{
    private const float MaximumHeightMargin = 0.2f;

    public bool isMultiplayerAgent = false;

    [Header("Agent Properties")]
    [SerializeField] protected float movementSpeed;
    [SerializeField] protected float rotateSpeed;
    [SerializeField] protected float throwStrength;

    [Header("Agent Hand Object")]
    [SerializeField] protected GameObject agentHand;
    [SerializeField] protected GameObject leftAgentHand;

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
    public float handMoveRange = 1f;
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
    private AgentBodyCollisionDetector _bodyCollisionDetector;
    private readonly AgentHandRuntime _leftHand = new AgentHandRuntime(AgentHandSide.Left);
    private readonly AgentHandRuntime _rightHand = new AgentHandRuntime(AgentHandSide.Right);

    // Body translation requested this physics step via MovePosition (a deferred move that
    // hasn't been applied to the transform yet). ApplyDesiredHandPose adds this so the hand
    // is pinned to where the body WILL be, not where it currently is.
    private Vector3 _pendingBodyTranslation;

    private bool _basketInView;
    private Vector3 _basketStoredPosition;
    private Quaternion _basketStoredRotation;
    private Transform _basketStoredParent;
    private Sequence _overheadChatSequence;
    private float _standingViewHeight;
    private float _standingMovementRootHeight;
    private bool _isCrouching;
    private AgentHandSide _lastManualHandSide = AgentHandSide.Left;

    private sealed class AgentHandRuntime
    {
        public AgentHandRuntime(AgentHandSide side)
        {
            Side = side;
        }

        public readonly AgentHandSide Side;
        public GameObject HandObject;
        public Animator Animator;
        public HandCollisionDetector CollisionDetector;
        public BoxCollider Collider;
        public Vector3 DefaultColliderSize;
        public Vector3 DefaultColliderCenter;
        public Vector3 InitialLocalPosition;
        public Quaternion InitialLocalRotation;
        public Rigidbody Rigidbody;
        public Vector3 DesiredLocalPosition;
        public Quaternion DesiredLocalRotation;
        public bool HasDesiredPose;
        public bool HasPendingPose;
        public bool IsGripped;
        public bool IsPointing;
        public float CurrentGrip;
        public float CurrentTrigger;
        public RuntimeRetailItem HeldItem;
        public DoorHandle GrabbedDoor;
    }

    protected virtual void Start()
    {
        rigidbody = GetComponentInParent<Rigidbody>() ?? GetComponent<Rigidbody>() ?? GetComponentInChildren<Rigidbody>();
        if (rigidbody != null)
        {
            _bodyCollisionDetector = rigidbody.GetComponent<AgentBodyCollisionDetector>();
            if (_bodyCollisionDetector == null)
                _bodyCollisionDetector = rigidbody.gameObject.AddComponent<AgentBodyCollisionDetector>();
        }
        _standingViewHeight = ViewTransform.position.y;
        _standingMovementRootHeight = MovementRoot.position.y;
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
        InitializeHandRuntime(_rightHand, agentHand);
        InitializeHandRuntime(_leftHand, leftAgentHand);
        SyncRightHandCompatibilityFields();
    }

    protected void InitializeHandRuntime(
        AgentHandSide side,
        GameObject handObject,
        Animator animator,
        HandCollisionDetector collisionDetector,
        BoxCollider handCollider)
    {
        AgentHandRuntime hand = GetHand(side);
        InitializeHandRuntime(hand, handObject, animator, collisionDetector, handCollider);
        SyncRightHandCompatibilityFields();
    }

    private void InitializeHandRuntime(AgentHandRuntime hand, GameObject handObject)
    {
        if (handObject == null) return;

        InitializeHandRuntime(
            hand,
            handObject,
            handObject.GetComponentInChildren<Animator>(),
            handObject.GetComponent<HandCollisionDetector>(),
            handObject.GetComponent<BoxCollider>());
    }

    private void InitializeHandRuntime(
        AgentHandRuntime hand,
        GameObject handObject,
        Animator animator,
        HandCollisionDetector collisionDetector,
        BoxCollider handCollider)
    {
        if (handObject == null) return;

        hand.HandObject = handObject;
        hand.Animator = animator;
        hand.CollisionDetector = collisionDetector;
        hand.InitialLocalPosition = handObject.transform.localPosition;
        hand.InitialLocalRotation = handObject.transform.localRotation;
        hand.Rigidbody = handObject.GetComponent<Rigidbody>();
        hand.DesiredLocalPosition = hand.InitialLocalPosition;
        hand.DesiredLocalRotation = hand.InitialLocalRotation;
        hand.HasDesiredPose = hand.Rigidbody != null;
        if (hand.Rigidbody != null)
        {
            hand.Rigidbody.useGravity = false;
            hand.Rigidbody.isKinematic = true;
            hand.Rigidbody.linearVelocity = Vector3.zero;
            hand.Rigidbody.angularVelocity = Vector3.zero;
        }
        hand.Collider = handCollider;
        if (hand.Collider != null)
        {
            hand.DefaultColliderSize = hand.Collider.size;
            hand.DefaultColliderCenter = hand.Collider.center;
        }

        SetupPhysicsActivationSphere(hand);
    }

    private void SetupPhysicsActivationSphere(AgentHandRuntime hand)
    {
        if (hand.HandObject == null) return;

        GameObject sphereObj = new GameObject("PhysicsActivationSphere");
        sphereObj.transform.SetParent(hand.HandObject.transform, worldPositionStays: false);
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

        if (Input.GetKey(KeyCode.Q) && _rightHand.HeldItem?.gameObject != null)
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

                if (_rightHand.HeldItem == null)
                {
                    Vector3 handLocation = transform.position
                                           + transform.forward * 0.2f
                                           + transform.right * 0.1f
                                           + transform.up * -0.1f;

                    ItemBBoxInfo itemBBoxInfo = hit.collider.GetComponent<ItemBBoxInfo>();
                    _rightHand.HeldItem = RetailItemRuntimeService.Instance.PickUpFromBBox(
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
        HandleCrouchInput();

        if (!isMultiplayerAgent && DataHandler.Instance.agentInteractionStyle == AgentInteractionStyle.Manual)
        {
            AgentHandSide? manualHandSide = GetManualHandControlSide();
            if (Input.GetKeyDown(KeyCode.Return) && manualHandSide.HasValue)
                ToggleGrip(manualHandSide.Value);
            if (Input.GetKeyDown(KeyCode.P)) TogglePoint();
            if (Input.GetKeyDown(KeyCode.X)) ToggleBasketInView();
        }
    }

    private void HandleMovement()
    {
        if (!isMultiplayerAgent)
        {
            Vector3 fwd = GetPlanarDirection(transform.forward, Vector3.forward);
            Vector3 right = GetPlanarDirection(transform.right, Vector3.right);
            float m = movementSpeed * Time.deltaTime;
            float r = rotateSpeed * Time.deltaTime;
            AgentHandSide? manualHandSide = GetManualHandControlSide();

            if (!manualHandSide.HasValue)
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
                    HandleManualHandControls(manualHandSide.Value);
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
        UpdateHandControlMode(_leftHand);
        UpdateHandControlMode(_rightHand);
    }

    private void UpdateHandControlMode(AgentHandRuntime hand)
    {
        if (hand.Rigidbody == null) return;

        bool isManualHand = IsManualHandControlActive(hand.Side);
        if (isManualHand)
        {
            // Start manual control from the live pose in case tracking drove the hand.
            hand.DesiredLocalPosition = hand.HandObject.transform.localPosition;
            hand.DesiredLocalRotation = hand.HandObject.transform.localRotation;
            hand.HasDesiredPose = true;
            hand.HasPendingPose = true;
        }

        if (!hand.Rigidbody.isKinematic)
        {
            hand.Rigidbody.linearVelocity = Vector3.zero;
            hand.Rigidbody.angularVelocity = Vector3.zero;
            hand.Rigidbody.useGravity = false;
            hand.Rigidbody.isKinematic = true;
        }
    }

    protected bool IsManualHandControlActive()
    {
        return GetManualHandControlSide().HasValue;
    }

    private bool IsManualHandControlActive(AgentHandSide side)
    {
        AgentHandSide? manualHandSide = GetManualHandControlSide();
        return manualHandSide.HasValue && manualHandSide.Value == side;
    }

    private AgentHandSide? GetManualHandControlSide()
    {
        bool leftPressed = Input.GetKeyDown(KeyCode.LeftShift);
        bool rightPressed = Input.GetKeyDown(KeyCode.RightShift);
        if (leftPressed) _lastManualHandSide = AgentHandSide.Left;
        if (rightPressed) _lastManualHandSide = AgentHandSide.Right;

        if (isMultiplayerAgent ||
            DataHandler.Instance == null ||
            DataHandler.Instance.agentInteractionStyle != AgentInteractionStyle.Manual)
            return null;

        bool leftHeld = Input.GetKey(KeyCode.LeftShift);
        bool rightHeld = Input.GetKey(KeyCode.RightShift);

        if (leftHeld && rightHeld) return _lastManualHandSide;
        if (leftHeld) return AgentHandSide.Left;
        if (rightHeld) return AgentHandSide.Right;
        return null;
    }

    private void ApplyDesiredHandPose()
    {
        ApplyDesiredHandPose(_leftHand);
        ApplyDesiredHandPose(_rightHand);
    }

    private void ApplyDesiredHandPose(AgentHandRuntime hand)
    {
        if (hand.Rigidbody == null || !hand.HasDesiredPose) return;
        // Re-pin the hand to its parent-local pose every step (even while kinematic) so a
        // nested Rigidbody isn't left behind when the body moves. The body's translation is
        // applied via a deferred MovePosition, so add _pendingBodyTranslation to target where
        // the body WILL be this step; rotation is written to the transform immediately, so
        // handParent.rotation is already current and needs no prediction.

        Transform handParent = hand.HandObject.transform.parent;
        Vector3 worldPosition = handParent != null
            ? handParent.TransformPoint(hand.DesiredLocalPosition) + _pendingBodyTranslation
            : hand.DesiredLocalPosition + _pendingBodyTranslation;
        Quaternion worldRotation = handParent != null
            ? handParent.rotation * hand.DesiredLocalRotation
            : hand.DesiredLocalRotation;

        hand.Rigidbody.MovePosition(worldPosition);
        hand.Rigidbody.MoveRotation(worldRotation);
        hand.HasPendingPose = false;
    }

    private Vector3 GetHandLocalPosition(AgentHandRuntime hand)
    {
        return hand.Rigidbody != null && hand.HasDesiredPose
            ? hand.DesiredLocalPosition
            : hand.HandObject.transform.localPosition;
    }

    private Quaternion GetHandLocalRotation(AgentHandRuntime hand)
    {
        return hand.Rigidbody != null && hand.HasDesiredPose
            ? hand.DesiredLocalRotation
            : hand.HandObject.transform.localRotation;
    }

    private void SetHandLocalPose(AgentHandRuntime hand, Vector3 localPosition, Quaternion localRotation)
    {
        if (hand.Rigidbody == null)
        {
            hand.HandObject.transform.localPosition = localPosition;
            hand.HandObject.transform.localRotation = localRotation;
            return;
        }

        hand.DesiredLocalPosition = localPosition;
        hand.DesiredLocalRotation = localRotation;
        hand.HasDesiredPose = true;
        hand.HasPendingPose = true;
    }

    private void SetHandWorldPosition(AgentHandRuntime hand, Vector3 worldPosition)
    {
        if (hand.Rigidbody == null)
        {
            hand.HandObject.transform.position = worldPosition;
            return;
        }

        Transform handParent = hand.HandObject.transform.parent;
        hand.DesiredLocalPosition = handParent != null
            ? handParent.InverseTransformPoint(worldPosition)
            : worldPosition;
        hand.HasDesiredPose = true;
        hand.HasPendingPose = true;
    }

    private void SetHandWorldPose(AgentHandRuntime hand, Vector3 worldPosition, Quaternion worldRotation)
    {
        if (hand.Rigidbody == null)
        {
            hand.HandObject.transform.position = worldPosition;
            hand.HandObject.transform.rotation = worldRotation;
            return;
        }

        Transform handParent = hand.HandObject.transform.parent;
        hand.DesiredLocalPosition = handParent != null
            ? handParent.InverseTransformPoint(worldPosition)
            : worldPosition;
        hand.DesiredLocalRotation = handParent != null
            ? Quaternion.Inverse(handParent.rotation) * worldRotation
            : worldRotation;
        hand.HasDesiredPose = true;
        hand.HasPendingPose = true;
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
        AnimateHand(_leftHand);
        AnimateHand(_rightHand);
        SyncRightHandCompatibilityFields();
    }

    private void AnimateHand(AgentHandRuntime hand)
    {
        if (hand.Animator == null) return;

        float gripTarget = hand.IsGripped || hand.IsPointing ? 1f : 0f;
        float triggerTarget = hand.IsPointing ? 1f : 0f;
        if (hand.IsGripped) triggerTarget = 0f;

        hand.CurrentGrip = Mathf.MoveTowards(hand.CurrentGrip, gripTarget, gripSpeed * Time.fixedDeltaTime);
        hand.CurrentTrigger = Mathf.MoveTowards(hand.CurrentTrigger, triggerTarget, gripSpeed * Time.fixedDeltaTime);

        hand.Animator.SetFloat("Grip", hand.CurrentGrip);
        hand.Animator.SetFloat("Trigger", hand.CurrentTrigger);
    }

    private void HandleManualHandControls(AgentHandSide side)
    {
        AgentHandRuntime hand = GetHand(side);
        if (hand.HandObject == null) return;

        if (hand.GrabbedDoor != null)
        {
            SetHandWorldPosition(hand, hand.GrabbedDoor.transform.position);
            DriveDoorFromInput(hand);
            return;
        }

        float speed = handMoveSpeed * Time.fixedDeltaTime;
        Vector3 localPos = GetHandLocalPosition(hand);

        if (Input.GetKey(KeyCode.E)) localPos += Vector3.up * speed;
        if (Input.GetKey(KeyCode.Q)) localPos -= Vector3.up * speed;
        if (Input.GetKey(KeyCode.W)) localPos += Vector3.forward * speed;
        if (Input.GetKey(KeyCode.S)) localPos -= Vector3.forward * speed;
        if (Input.GetKey(KeyCode.A)) localPos -= Vector3.right * speed;
        if (Input.GetKey(KeyCode.D)) localPos += Vector3.right * speed;

        // if (localPos.magnitude > handMoveRange)
        //     localPos = localPos.normalized * handMoveRange;

        SetHandLocalPose(hand, localPos, GetHandLocalRotation(hand));
    }

    private void DriveDoorFromInput(AgentHandRuntime hand)
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

        HingeJoint hinge = hand.GrabbedDoor.Hinge;
        Rigidbody doorRb = hand.GrabbedDoor.DoorRigidbody;

        Vector3 axisWorld = doorRb.transform.TransformDirection(hinge.axis);
        Vector3 anchorWorld = doorRb.transform.TransformPoint(hinge.anchor);
        Vector3 toHandle = hand.GrabbedDoor.transform.position - anchorWorld;
        float radius = toHandle.magnitude;
        if (radius < 0.001f) return;

        Vector3 tangent = Vector3.Cross(axisWorld, toHandle.normalized);
        float tangentialAmount = Vector3.Dot(inputWorld, tangent);

        hand.GrabbedDoor.DoorBuilder.ApplyHandForce(tangent * (tangentialAmount * doorHandleForce));
    }

    public bool IsBasketInView => _basketInView;

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

    // Egocentric translation: +z = planar forward, +x = planar right, +y = world up.
    // Pitch is ignored so looking up/down never steers movement into the floor/ceiling.
    public Vector3 EgocentricToWorldTranslation(Vector3 localTranslation)
    {
        Vector3 forward = GetPlanarDirection(transform.forward, Vector3.forward);
        Vector3 right = GetPlanarDirection(transform.right, Vector3.right);
        return right * localTranslation.x
               + Vector3.up * localTranslation.y
               + forward * localTranslation.z;
    }

    public Vector3 ClampTranslationToMaximumHeight(Vector3 deltaTranslation)
    {
        float targetHeight = MovementRoot.position.y + _pendingBodyTranslation.y + deltaTranslation.y;
        if (targetHeight > MaximumMovementRootHeight)
            deltaTranslation.y -= targetHeight - MaximumMovementRootHeight;
        return deltaTranslation;
    }

    public void TransformHand(Vector3 localPosition, Vector3 eulerRotation, AgentHandSide side = AgentHandSide.Right)
    {
        AgentHandRuntime hand = GetHand(side);
        if (hand.HandObject == null) return;
        if (localPosition.magnitude > handMoveRange) return;
        SetHandWorldPose(
            hand,
            transform.TransformPoint(localPosition),
            transform.rotation * Quaternion.Euler(eulerRotation));
    }

    public void TranslateHand(Vector3 deltaLocalPosition, Vector3 deltaRotation, AgentHandSide side = AgentHandSide.Right)
    {
        AgentHandRuntime hand = GetHand(side);
        if (hand.HandObject == null) return;
        Vector3 localPos = GetHandLocalPosition(hand) + deltaLocalPosition;
        if (localPos.magnitude > handMoveRange)
            localPos = localPos.normalized * handMoveRange;
        Quaternion localRotation = GetHandLocalRotation(hand) * Quaternion.Euler(deltaRotation);
        SetHandLocalPose(hand, localPos, localRotation);
    }

    public void ResetHandPosition(AgentHandSide side = AgentHandSide.Right)
    {
        AgentHandRuntime hand = GetHand(side);
        if (hand.HandObject == null) return;
        SetHandLocalPose(hand, hand.InitialLocalPosition, hand.InitialLocalRotation);
    }

    public Transform MovementRoot =>
        rigidbody != null
            ? rigidbody.transform
            : GetComponentInParent<Rigidbody>()?.transform ?? transform;

    public Transform ViewTransform => transform;

    public Transform HandTransform => RightHandTransform;

    public Transform RightHandTransform => _rightHand.HandObject != null ? _rightHand.HandObject.transform : null;

    public Transform LeftHandTransform => _leftHand.HandObject != null ? _leftHand.HandObject.transform : null;

    public float StandingViewHeight => _standingViewHeight;

    public float MaximumViewHeight => _standingViewHeight + MaximumHeightMargin;

    public float MaximumMovementRootHeight => _standingMovementRootHeight + MaximumHeightMargin;

    public bool IsAgentColliding => _bodyCollisionDetector != null && _bodyCollisionDetector.IsColliding;

    public bool IsGripped => _rightHand.IsGripped;

    public bool IsLeftGripped => _leftHand.IsGripped;

    public bool IsPointing => _rightHand.IsPointing;

    public bool IsLeftPointing => _leftHand.IsPointing;

    public string RightHandHoveredItemId => _rightHand.CollisionDetector?.DetectedItemBBoxInfo?.itemId;

    public string LeftHandHoveredItemId => _leftHand.CollisionDetector?.DetectedItemBBoxInfo?.itemId;

    public float GripAmount => _rightHand.CurrentGrip;

    public float TriggerAmount => _rightHand.CurrentTrigger;

    public float LeftGripAmount => _leftHand.CurrentGrip;

    public float LeftTriggerAmount => _leftHand.CurrentTrigger;

    public bool IsHoldingItem() => _rightHand.HeldItem?.gameObject != null;

    public bool IsHoldingItem(AgentHandSide side) => GetHand(side).HeldItem?.gameObject != null;

    protected float CurrentLocalViewHeight => _isCrouching ? _standingViewHeight * 0.5f : _standingViewHeight;

    protected void HandleCrouchInput()
    {
        if (isMultiplayerAgent) return;

        bool shouldCrouch = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        if (_isCrouching == shouldCrouch) return;

        _isCrouching = shouldCrouch;
        Vector3 position = transform.position;
        position.y = CurrentLocalViewHeight;
        transform.position = position;
    }

    public void TogglePoint(AgentHandSide side = AgentHandSide.Right)
    {
        AgentHandRuntime hand = GetHand(side);
        hand.IsPointing = !hand.IsPointing;
        hand.IsGripped = false;
        if (hand.CollisionDetector != null) hand.CollisionDetector.IsPointing = hand.IsPointing;
        if (hand.Collider != null)
        {
            if (hand.IsPointing)
            {
                hand.Collider.center = new Vector3(0.06f, -0.01f, 0.04f);
                Vector3 s = hand.Collider.size;
                s.y = 0.02f;
                s.z = 0.13f;
                hand.Collider.size = s;
            }
            else
            {
                hand.Collider.center = hand.DefaultColliderCenter;
                hand.Collider.size = hand.DefaultColliderSize;
            }
        }

        SyncRightHandCompatibilityFields();
    }

    public void ToggleGrip(AgentHandSide side = AgentHandSide.Right)
    {
        AgentHandRuntime hand = GetHand(side);
        if (!hand.IsGripped)
        {
            // Turn off pointing mode (ensures bounding box is at palm)
            hand.IsPointing = false;
            
            if (hand.CollisionDetector != null) hand.CollisionDetector.IsPointing = false;
            
            // Reset our box collider to the "grip" collider
            if (hand.Collider != null)
            {
                hand.Collider.center = hand.DefaultColliderCenter;
                hand.Collider.size = hand.DefaultColliderSize;
            }
            
            if (hand.CollisionDetector != null && hand.CollisionDetector.DetectedDoorHandle != null)
            {
                hand.GrabbedDoor = hand.CollisionDetector.DetectedDoorHandle;
                UpdateDoorCollisionIgnore();
            }
            else if (hand.HandObject != null &&
                     hand.CollisionDetector != null &&
                     hand.CollisionDetector.DetectedItem != null &&
                     hand.CollisionDetector.DetectedItemBBoxInfo != null)
            {
                InstantiateItemFromBBox(hand);
            }
            hand.IsGripped = true;
        }
        else
        {
            // If we're currently grabbing a door, un-grab it
            if (hand.GrabbedDoor != null)
            {
                ResetHandPosition(side);
                hand.GrabbedDoor.DoorRigidbody.linearVelocity = Vector3.zero;
                hand.GrabbedDoor.DoorRigidbody.angularVelocity = Vector3.zero;
                hand.GrabbedDoor = null;
                UpdateDoorCollisionIgnore();
            }
            hand.IsGripped = false;
        }

        if (!hand.IsGripped && hand.HeldItem?.gameObject != null)
            DropCurrentlyHeldItem(hand);

        SyncRightHandCompatibilityFields();
    }

    private void DropCurrentlyHeldItem(AgentHandRuntime hand)
    {
        RetailItemRuntimeService.Instance.DropHeldItem(hand.HeldItem, _itemBBoxMaterial);
        hand.HeldItem = null;
    }

    private void InstantiateItemFromBBox(AgentHandRuntime hand)
    {
        ItemBBoxInfo itemBBoxInfo = hand.CollisionDetector.DetectedItemBBoxInfo;
        hand.HeldItem = RetailItemRuntimeService.Instance.PickUpFromBBox(
            itemBBoxInfo,
            hand.HandObject.transform,
            hand.HandObject.transform.position - new Vector3(0, 0.1f, 0),
            transform.rotation,
            new Vector3(0f, -60f, 0f)
        );
    }

    private void ThrowItem()
    {
        RetailItemRuntimeService.Instance.ThrowHeldItem(
            _rightHand.HeldItem,
            _itemBBoxMaterial,
            transform.forward * throwStrength);
        _rightHand.HeldItem = null;
    }

    private Vector3 GetPlanarDirection(Vector3 direction, Vector3 fallback)
    {
        direction.y = 0f;
        if (direction.sqrMagnitude > 0.0001f) return direction.normalized;

        fallback.y = 0f;
        return fallback.sqrMagnitude > 0.0001f ? fallback.normalized : Vector3.forward;
    }

    private AgentHandRuntime GetHand(AgentHandSide side)
    {
        return side == AgentHandSide.Left ? _leftHand : _rightHand;
    }

    private void UpdateDoorCollisionIgnore()
    {
        Physics.IgnoreLayerCollision(
            9,
            12,
            _leftHand.GrabbedDoor != null || _rightHand.GrabbedDoor != null);
    }

    private void SyncRightHandCompatibilityFields()
    {
        handAnimator = _rightHand.Animator;
        _handCollisionDetector = _rightHand.CollisionDetector;
        _handCollider = _rightHand.Collider;
        _defaultColliderSize = _rightHand.DefaultColliderSize;
        _defaultColliderCenter = _rightHand.DefaultColliderCenter;
        _initialHandLocalPosition = _rightHand.InitialLocalPosition;
        _initialHandLocalRotation = _rightHand.InitialLocalRotation;
        isGripped = _rightHand.IsGripped;
        isPointing = _rightHand.IsPointing;
        currentGrip = _rightHand.CurrentGrip;
        currentTrigger = _rightHand.CurrentTrigger;
    }
}

public class AgentBodyCollisionDetector : MonoBehaviour
{
    private readonly HashSet<Collider> _blockingColliders = new();

    public bool IsColliding
    {
        get
        {
            _blockingColliders.RemoveWhere(collider => collider == null);
            return _blockingColliders.Count > 0;
        }
    }

    private void OnCollisionEnter(Collision collision)
    {
        RefreshCollision(collision);
    }

    private void OnCollisionStay(Collision collision)
    {
        RefreshCollision(collision);
    }

    private void OnCollisionExit(Collision collision)
    {
        if (collision.collider != null)
            _blockingColliders.Remove(collision.collider);
    }

    private void RefreshCollision(Collision collision)
    {
        Collider otherCollider = collision.collider;
        if (otherCollider == null) return;

        if (HasBlockingContact(collision))
            _blockingColliders.Add(otherCollider);
        else
            _blockingColliders.Remove(otherCollider);
    }

    private static bool HasBlockingContact(Collision collision)
    {
        for (int i = 0; i < collision.contactCount; i++)
        {
            Vector3 normal = collision.GetContact(i).normal;
            float horizontalSqrMagnitude = normal.x * normal.x + normal.z * normal.z;

            // Ignore floor- and ceiling-like contacts; keep steep contacts that can block travel.
            if (horizontalSqrMagnitude >= normal.y * normal.y)
                return true;
        }

        return false;
    }
}
