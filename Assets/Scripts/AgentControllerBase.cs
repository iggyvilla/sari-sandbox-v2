using System;
using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;

public enum AgentHandSide
{
    Left,
    Right
}

// Animator parameter hashes shared by agent controllers and ghost followers.
public static class AgentAnimatorParams
{
    public static readonly int Speed = Animator.StringToHash("Speed");
    public static readonly int Grip = Animator.StringToHash("Grip");
    public static readonly int Trigger = Animator.StringToHash("Trigger");
    public static readonly int IsWalking = Animator.StringToHash("isWalking");
    public static readonly int IsWalkingBackward = Animator.StringToHash("isWalkingBackward");
    public static readonly int IsWalkingLeft = Animator.StringToHash("isWalkingLeft");
    public static readonly int IsWalkingRight = Animator.StringToHash("isWalkingRight");
}

public abstract class AgentControllerBase : MonoBehaviour
{
    private const float MaximumHeightMargin = 0.2f;
    private const float MinimumFloorScale = 0.0001f;
    private const float ChatVisibleSeconds = 4f;
    private const float ChatFadeSeconds = 1f;
    private static readonly Vector3 HeldItemEulerOffset = new Vector3(0f, -60f, 0f);
    private static readonly Vector3 PointingColliderCenter = new Vector3(0.06f, -0.01f, 0.04f);
    private const float PointingColliderHeight = 0.02f;
    private const float PointingColliderDepth = 0.13f;

    // Number of agents currently holding a door; hand/door layer collisions stay ignored while > 0.
    private static int s_agentsHoldingDoors;

    public bool isMultiplayerAgent = false;

    [Header("Agent Properties")]
    [SerializeField] protected float movementSpeed;
    [SerializeField] protected float rotateSpeed;
    [SerializeField] protected float throwStrength;

    [Header("Out Of Bounds Recovery")]
    [SerializeField, Min(0f)] private float floorBoundsPadding = 0.05f;

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

    private LayerMask interactableLayerMask;
    private AgentBodyCollisionDetector _bodyCollisionDetector;
    private readonly AgentHandRuntime _leftHand = new AgentHandRuntime();
    private readonly AgentHandRuntime _rightHand = new AgentHandRuntime();

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
    private Transform _floorTransform;
    private Bounds _floorLocalBounds;
    private Vector3 _spawnPosition;
    private bool _hasFloorBounds;
    private int _outOfBoundsRecoveryCount;
    private bool _isHoldingDoor;
    private bool _gazeActivateRequested;
    private Transform _lastGazeTransform;

    /// <summary>
    /// Fired once after each authoritative out-of-bounds recovery. The pose is absolute and can
    /// be used by remote clients to replace any delta-derived state.
    /// </summary>
    public event Action<AgentControllerBase, int, Vector3, Quaternion> OutOfBoundsRecovered;

    private sealed class AgentHandRuntime
    {
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
        public bool IsGripped;
        public bool IsPointing;
        public float CurrentGrip;
        public float CurrentTrigger;
        public RuntimeRetailItem HeldItem;
        public DoorHandle GrabbedDoor;
    }

    protected virtual void Start()
    {
        // GetComponentInParent includes this GameObject; explicit checks avoid Unity's fake-null with ??.
        rigidbody = GetComponentInParent<Rigidbody>();
        if (rigidbody == null) rigidbody = GetComponentInChildren<Rigidbody>();
        if (rigidbody != null)
        {
            _bodyCollisionDetector = rigidbody.GetComponent<AgentBodyCollisionDetector>();
            if (_bodyCollisionDetector == null)
                _bodyCollisionDetector = rigidbody.gameObject.AddComponent<AgentBodyCollisionDetector>();
        }
        _standingViewHeight = ViewTransform.position.y;
        _standingMovementRootHeight = MovementRoot.position.y;
        InitializeOutOfBoundsRecovery();
        interactableLayerMask = LayerMask.GetMask("SariInteractable");
        InitializeHandComponents();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetDoorGrabCount() => s_agentsHoldingDoors = 0;

    protected virtual void OnDestroy()
    {
        _overheadChatSequence?.Kill();

        // Release this agent's share of the global hand/door collision ignore.
        _leftHand.GrabbedDoor = null;
        _rightHand.GrabbedDoor = null;
        UpdateDoorCollisionIgnore();
    }

    public void ShowChat(string chatText)
    {
        if (string.IsNullOrEmpty(chatText) || overheadChatText == null) return;

        _overheadChatSequence?.Kill();
        overheadChatText.text = chatText;
        overheadChatText.alpha = 1f;

        _overheadChatSequence = DOTween.Sequence()
            .AppendInterval(ChatVisibleSeconds)
            .Append(DOTween.To(
                () => overheadChatText.alpha,
                alpha => overheadChatText.alpha = alpha,
                0f,
                ChatFadeSeconds));
    }

    protected virtual void InitializeHandComponents()
    {
        InitializeHandRuntime(_rightHand, agentHand);
        InitializeHandRuntime(_leftHand, leftAgentHand);
    }

    protected void InitializeHandRuntime(
        AgentHandSide side,
        GameObject handObject,
        Animator animator,
        HandCollisionDetector collisionDetector,
        BoxCollider handCollider)
    {
        InitializeHandRuntime(GetHand(side), handObject, animator, collisionDetector, handCollider);
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
        RecoverIfOutOfBounds();
        AgentHandSide? manualHandSide = GetManualHandControlSide();
        UpdateHandControlMode(manualHandSide);
        HandleMovement(manualHandSide);
        ApplyDesiredHandPose();
        if (isMultiplayerAgent) return;

        // Consume the Update-latched press so it fires once per key press, not once per physics step.
        bool activatePressed = _gazeActivateRequested;
        _gazeActivateRequested = false;

        if (IsManualInteraction) return;

        // Gaze Mode

        if (Input.GetKey(KeyCode.Q) && HasHeldItem(_rightHand))
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

            if (hit.transform != _lastGazeTransform)
            {
                _lastGazeTransform = hit.transform;
                if (SariUIHandler.Instance != null) SariUIHandler.Instance.UpdateInfoText(hit.transform.name);
            }

            OutlineController outlineControllerScript = hit.collider.GetComponent<OutlineController>();
            if (outlineControllerScript) outlineControllerScript.OnGaze();

            if (activatePressed || Input.GetKey(KeyCode.Return))
            {
                HingedDoorBuilder hingedDoorHandler = hit.collider.GetComponentInParent<HingedDoorBuilder>();
                if (hingedDoorHandler != null)
                {
                    if (activatePressed) hingedDoorHandler.ToggleDoor();
                    return;
                }

                ItemBBoxInfo itemBBoxInfo = hit.collider.GetComponent<ItemBBoxInfo>();
                if (_rightHand.HeldItem == null && itemBBoxInfo != null)
                {
                    Vector3 handLocation = transform.position
                                           + transform.forward * 0.2f
                                           + transform.right * 0.1f
                                           + transform.up * -0.1f;

                    _rightHand.HeldItem = RetailItemRuntimeService.Instance.PickUpFromBBox(
                        itemBBoxInfo,
                        transform,
                        handLocation,
                        transform.rotation,
                        HeldItemEulerOffset
                    );
                }
            }
        }
    }

    private static bool IsManualInteraction =>
        DataHandler.Instance != null &&
        DataHandler.Instance.agentInteractionStyle == AgentInteractionStyle.Manual;

    private static bool HasHeldItem(AgentHandRuntime hand) => hand.HeldItem?.gameObject != null;

    private void InitializeOutOfBoundsRecovery()
    {
        _spawnPosition = !isMultiplayerAgent && DataHandler.Instance != null
            ? DataHandler.Instance.agentSpawnPosition
            : MovementRoot.position;

        GameObject floor = DataHandler.Instance != null ? DataHandler.Instance.floor : null;
        if (floor == null)
        {
            RoomStructure roomStructure = FindFirstObjectByType<RoomStructure>();
            floor = roomStructure != null ? roomStructure.gameObject : null;
        }

        MeshCollider floorCollider = floor != null ? floor.GetComponent<MeshCollider>() : null;
        Mesh floorMesh = floorCollider != null ? floorCollider.sharedMesh : null;
        if (floorMesh == null)
        {
            Debug.LogWarning(
                $"{name} cannot enable out-of-bounds recovery because no floor mesh collider was found.",
                this);
            return;
        }

        // Use the collider mesh rather than Renderer.localBounds. Static batching can alter the
        // renderer's local bounds, while the collider retains the source plane footprint that
        // RoomStructure scales at runtime.
        _floorTransform = floorCollider.transform;
        _floorLocalBounds = floorMesh.bounds;
        _hasFloorBounds = true;

        if (IsOutsideFloorBounds(_spawnPosition))
        {
            _hasFloorBounds = false;
            Debug.LogWarning(
                $"{name} cannot enable out-of-bounds recovery because its spawn position " +
                $"{_spawnPosition} is outside the padded floor bounds.",
                this);
        }
    }

    /// <summary>
    /// Recover the agent to its spawn when its movement root is outside the floor footprint.
    /// Safe to call from FixedUpdate and again immediately before serializing a pose: after the
    /// first call the root is in bounds, so the counter and event cannot repeat.
    /// </summary>
    public bool RecoverIfOutOfBounds()
    {
        Transform movementRoot = MovementRoot;
        if (!_hasFloorBounds || _floorTransform == null || movementRoot == null) return false;

        Vector3 currentPosition = movementRoot.position;
        if (!IsOutsideFloorBounds(currentPosition)) return false;

        // A deferred MovePosition from the command that crossed the boundary must not survive
        // the correction and get added to the next hand/body update.
        _pendingBodyTranslation = Vector3.zero;

        if (rigidbody != null)
        {
            rigidbody.linearVelocity = Vector3.zero;
            rigidbody.angularVelocity = Vector3.zero;
            rigidbody.position = _spawnPosition;
        }
        // Rigidbody.position only reaches the Transform after the next simulation step.
        movementRoot.position = _spawnPosition;

        ReleaseTransientEnvironmentalConstraints();
        _outOfBoundsRecoveryCount++;

        Vector3 recoveredPosition = movementRoot.position;
        Quaternion recoveredRotation = ViewTransform.rotation;

        Debug.LogWarning(
            $"{name} left the floor bounds at {currentPosition} and was returned to " +
            $"its spawn position {_spawnPosition} (recovery {_outOfBoundsRecoveryCount}).",
            this);

        OutOfBoundsRecovered?.Invoke(
            this,
            _outOfBoundsRecoveryCount,
            recoveredPosition,
            recoveredRotation);
        return true;
    }

    private void ReleaseTransientEnvironmentalConstraints()
    {
        bool releasedDoor = ReleaseGrabbedDoor(_leftHand);
        releasedDoor |= ReleaseGrabbedDoor(_rightHand);
        if (releasedDoor) UpdateDoorCollisionIgnore();
    }

    private static bool ReleaseGrabbedDoor(AgentHandRuntime hand)
    {
        if (hand.GrabbedDoor == null) return false;

        Rigidbody doorRigidbody = hand.GrabbedDoor.DoorRigidbody;
        if (doorRigidbody != null)
        {
            doorRigidbody.linearVelocity = Vector3.zero;
            doorRigidbody.angularVelocity = Vector3.zero;
        }

        hand.GrabbedDoor = null;
        // A door-only grip is transient environmental state. Never open a hand that is actually
        // carrying a retail item, even if malformed scene state associated it with both.
        if (!HasHeldItem(hand)) hand.IsGripped = false;
        return true;
    }

    private bool IsOutsideFloorBounds(Vector3 worldPosition)
    {
        Vector3 floorPosition = _floorTransform.InverseTransformPoint(worldPosition);
        Vector3 floorScale = _floorTransform.lossyScale;
        float paddingX = floorBoundsPadding / Mathf.Max(Mathf.Abs(floorScale.x), MinimumFloorScale);
        float paddingY = floorBoundsPadding / Mathf.Max(Mathf.Abs(floorScale.y), MinimumFloorScale);
        float paddingZ = floorBoundsPadding / Mathf.Max(Mathf.Abs(floorScale.z), MinimumFloorScale);

        return !IsFinite(floorPosition) ||
               floorPosition.x < _floorLocalBounds.min.x - paddingX ||
               floorPosition.x > _floorLocalBounds.max.x + paddingX ||
               floorPosition.y < _floorLocalBounds.min.y - paddingY ||
               floorPosition.z < _floorLocalBounds.min.z - paddingZ ||
               floorPosition.z > _floorLocalBounds.max.z + paddingZ;
    }

    private static bool IsFinite(Vector3 value)
    {
        return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
               !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
               !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }

    protected virtual void Update()
    {
        HandleCrouchInput();
        if (isMultiplayerAgent) return;

        if (Input.GetKeyDown(KeyCode.Return)) _gazeActivateRequested = true;

        if (IsManualInteraction)
        {
            AgentHandSide? manualHandSide = GetManualHandControlSide();
            if (Input.GetKeyDown(KeyCode.Return) && manualHandSide.HasValue)
                ToggleGrip(manualHandSide.Value);
            if (Input.GetKeyDown(KeyCode.P)) TogglePoint();
            if (Input.GetKeyDown(KeyCode.X)) ToggleBasketInView();
        }
    }

    private void HandleMovement(AgentHandSide? manualHandSide)
    {
        if (!isMultiplayerAgent)
        {
            Vector3 fwd = GetPlanarDirection(transform.forward, Vector3.forward);
            Vector3 right = GetPlanarDirection(transform.right, Vector3.right);
            float m = movementSpeed * Time.deltaTime;
            float r = rotateSpeed * Time.deltaTime;

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
                // GetManualHandControlSide only returns a side in Manual interaction mode.
                HandleManualHandControls(manualHandSide.Value);
            }
        }

        AnimateHand();
        AnimateBody();

        Vector3 e = transform.eulerAngles;
        e.z = 0;
        transform.rotation = Quaternion.Euler(e);
    }

    private void UpdateHandControlMode(AgentHandSide? manualHandSide)
    {
        UpdateHandControlMode(_leftHand, manualHandSide == AgentHandSide.Left);
        UpdateHandControlMode(_rightHand, manualHandSide == AgentHandSide.Right);
    }

    private void UpdateHandControlMode(AgentHandRuntime hand, bool isManualHand)
    {
        if (hand.Rigidbody == null) return;

        if (isManualHand)
        {
            // Start manual control from the live pose in case tracking drove the hand.
            hand.DesiredLocalPosition = hand.HandObject.transform.localPosition;
            hand.DesiredLocalRotation = hand.HandObject.transform.localRotation;
            hand.HasDesiredPose = true;
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

    private AgentHandSide? GetManualHandControlSide()
    {
        bool leftPressed = Input.GetKeyDown(KeyCode.LeftShift);
        bool rightPressed = Input.GetKeyDown(KeyCode.RightShift);
        if (leftPressed) _lastManualHandSide = AgentHandSide.Left;
        if (rightPressed) _lastManualHandSide = AgentHandSide.Right;

        if (isMultiplayerAgent || !IsManualInteraction)
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
    }

    private void AnimateHand(AgentHandRuntime hand)
    {
        if (hand.Animator == null) return;

        float gripTarget = hand.IsGripped || hand.IsPointing ? 1f : 0f;
        float triggerTarget = hand.IsPointing ? 1f : 0f;
        if (hand.IsGripped) triggerTarget = 0f;

        hand.CurrentGrip = Mathf.MoveTowards(hand.CurrentGrip, gripTarget, gripSpeed * Time.fixedDeltaTime);
        hand.CurrentTrigger = Mathf.MoveTowards(hand.CurrentTrigger, triggerTarget, gripSpeed * Time.fixedDeltaTime);

        hand.Animator.SetFloat(AgentAnimatorParams.Grip, hand.CurrentGrip);
        hand.Animator.SetFloat(AgentAnimatorParams.Trigger, hand.CurrentTrigger);
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
        Vector3 localPos = GetHandLocalPosition(hand) + ReadHandInputAxis() * speed;
        SetHandLocalPose(hand, localPos, GetHandLocalRotation(hand));
    }

    // WASD = planar, E/Q = up/down; unnormalized sum of the held keys.
    private static Vector3 ReadHandInputAxis()
    {
        Vector3 input = Vector3.zero;
        if (Input.GetKey(KeyCode.W)) input += Vector3.forward;
        if (Input.GetKey(KeyCode.S)) input -= Vector3.forward;
        if (Input.GetKey(KeyCode.A)) input -= Vector3.right;
        if (Input.GetKey(KeyCode.D)) input += Vector3.right;
        if (Input.GetKey(KeyCode.E)) input += Vector3.up;
        if (Input.GetKey(KeyCode.Q)) input -= Vector3.up;
        return input;
    }

    private void DriveDoorFromInput(AgentHandRuntime hand)
    {
        Vector3 inputLocal = ReadHandInputAxis();
        if (inputLocal.sqrMagnitude < 0.001f) return;

        Vector3 inputWorld = transform.TransformDirection(inputLocal.normalized);

        HingeJoint hinge = hand.GrabbedDoor.Hinge;
        Rigidbody doorRb = hand.GrabbedDoor.DoorRigidbody;
        if (hinge == null || doorRb == null) return;

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
        // MovePosition is deferred (and doesn't sweep); record the delta so ApplyDesiredHandPose keeps the hand in sync.
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

    public Transform MovementRoot
    {
        get
        {
            Rigidbody body = rigidbody != null ? rigidbody : GetComponentInParent<Rigidbody>();
            return body != null ? body.transform : transform;
        }
    }

    public Transform ViewTransform => transform;

    public Transform HandTransform => RightHandTransform;

    public Transform RightHandTransform => _rightHand.HandObject != null ? _rightHand.HandObject.transform : null;

    public Transform LeftHandTransform => _leftHand.HandObject != null ? _leftHand.HandObject.transform : null;

    public float MaximumMovementRootHeight => _standingMovementRootHeight + MaximumHeightMargin;

    public bool IsAgentColliding => _bodyCollisionDetector != null && _bodyCollisionDetector.IsColliding;

    public int OutOfBoundsRecoveryCount => _outOfBoundsRecoveryCount;

    public bool IsGripped => _rightHand.IsGripped;

    public bool IsLeftGripped => _leftHand.IsGripped;

    public bool IsPointing => _rightHand.IsPointing;

    public bool IsLeftPointing => _leftHand.IsPointing;

    public string RightHandHoveredItemId => GetHoveredItemId(_rightHand);

    public string LeftHandHoveredItemId => GetHoveredItemId(_leftHand);

    private static string GetHoveredItemId(AgentHandRuntime hand)
    {
        ItemBBoxInfo info = hand.CollisionDetector != null ? hand.CollisionDetector.DetectedItemBBoxInfo : null;
        return info != null ? info.itemId : null;
    }

    public float GripAmount => _rightHand.CurrentGrip;

    public float TriggerAmount => _rightHand.CurrentTrigger;

    public bool IsHoldingItem() => HasHeldItem(_rightHand);

    public bool IsHoldingItem(AgentHandSide side) => HasHeldItem(GetHand(side));

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
        if (hand.IsPointing && hand.Collider != null)
        {
            hand.Collider.center = PointingColliderCenter;
            Vector3 s = hand.Collider.size;
            s.y = PointingColliderHeight;
            s.z = PointingColliderDepth;
            hand.Collider.size = s;
        }
        else
        {
            ResetHandCollider(hand);
        }
    }

    private static void ResetHandCollider(AgentHandRuntime hand)
    {
        if (hand.Collider == null) return;
        hand.Collider.center = hand.DefaultColliderCenter;
        hand.Collider.size = hand.DefaultColliderSize;
    }

    public void ToggleGrip(AgentHandSide side = AgentHandSide.Right)
    {
        AgentHandRuntime hand = GetHand(side);
        if (!hand.IsGripped)
        {
            // Turn off pointing mode so the grip collider sits at the palm.
            hand.IsPointing = false;
            ResetHandCollider(hand);

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
            if (hand.GrabbedDoor != null)
            {
                ResetHandPosition(side);
                ReleaseGrabbedDoor(hand);
                UpdateDoorCollisionIgnore();
            }
            hand.IsGripped = false;
        }

        if (!hand.IsGripped && HasHeldItem(hand))
            DropCurrentlyHeldItem(hand);
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
            HeldItemEulerOffset
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

    // Physics.IgnoreLayerCollision is global, so ref-count across agents.
    private void UpdateDoorCollisionIgnore()
    {
        bool isHoldingDoor = _leftHand.GrabbedDoor != null || _rightHand.GrabbedDoor != null;
        if (isHoldingDoor == _isHoldingDoor) return;

        _isHoldingDoor = isHoldingDoor;
        s_agentsHoldingDoors = Mathf.Max(0, s_agentsHoldingDoors + (isHoldingDoor ? 1 : -1));
        Physics.IgnoreLayerCollision(
            LayerMask.NameToLayer("AgentHand"),
            LayerMask.NameToLayer("HingeDoor"),
            s_agentsHoldingDoors > 0);
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
