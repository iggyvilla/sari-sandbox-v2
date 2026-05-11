using UnityEngine;
using UnityEngine.AI;

public class AgentController : MonoBehaviour
{
    [Header("Agent Properties")]
    [SerializeField] float movementSpeed;
    [SerializeField] float rotateSpeed;
    [SerializeField] float throwStrength;
    
    [Header("Agent Hand Object")]
    [SerializeField] GameObject agentHand;

    [Header("Manual Hand Control")]
    public float handMoveRange = 0.5f;
    public float handMoveSpeed = 1f;
    public float gripSpeed = 2f;
    public float doorHandleForce = 5f;

    private Rigidbody rigidbody;
    private LayerMask interactableLayerMask;
    private GameObject rightHandItem;
    private bool rightHandUsed;
    private Animator handAnimator;
    private float currentGrip;
    private float currentTrigger;
    private bool isGripped;
    private bool isPointing;
    private HandCollisionDetector _handCollisionDetector;
    private Vector3 _initialHandLocalPosition;
    private Quaternion _initialHandLocalRotation;
    private DoorHandle _grabbedDoor;
    private BoxCollider _handCollider;
    private Vector3 _defaultColliderSize;
    private Vector3 _defaultColliderCenter;
    
    [Header("VoxeLLMap")]
    /* VoxeLLMap-related variables */
    private NavMeshAgent _agent;
    public GameObject target;
    
    void Start()
    {
        rigidbody = GetComponentInParent<Rigidbody>();
        rightHandUsed = false;
        interactableLayerMask = LayerMask.GetMask("SariInteractable");
        _agent = GetComponent<NavMeshAgent>();
        if (agentHand != null)
        {
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
        }
    }
    
    void FixedUpdate()
    {
        HandleMovement();
        
        RaycastHit hit;

        Debug.DrawRay(transform.position, transform.TransformDirection(Vector3.forward) * 10f, Color.yellow);

        /* "Gaze"-style item interaction */
        if (DataHandler.Instance.agentInteractionStyle ==
            AgentInteractionStyle.Manual) return;
        
        if (Input.GetKey(KeyCode.Q) && rightHandUsed)
        {
            ThrowItem(rightHandItem);
            rightHandUsed = false;
        }
        
        if (
            Physics.Raycast(
                transform.position,
                transform.TransformDirection(Vector3.forward),
                out hit,
                Mathf.Infinity,
                interactableLayerMask
            )
        )
        {
            if (hit.collider.CompareTag("Wall")) return;
            
            string hitName = hit.transform.name;
            
            // Update debug UI to show item we're currently looking at
            SariUIHandler.Instance.UpdateInfoText(hitName);
            
            // If the hit interactable object  
            // should show an outline, enable it
            OutlineController outlineControllerScript = hit.collider.GetComponent<OutlineController>();
            if (outlineControllerScript) outlineControllerScript.OnGaze();
            
            // For "grabbing" items/opening doors
            if (Input.GetKey(KeyCode.Return))
            {
                HingedDoorBuilder hingedDoorHandler = hit.collider.GetComponentInParent<HingedDoorBuilder>();
                
                // This is only true if the raycast hit a door
                if (hingedDoorHandler != null)
                {
                    // If it's a door, it'll have hingedDoorHandler, open it
                    hingedDoorHandler.ToggleDoor();
                    return;
                }
                
                if (!rightHandUsed)
                {
                    var selectedItem =
                    Resources.Load<GameObject>("Prefabs/Products/" + hitName);
                    selectedItem.transform.position = Vector3.zero;
                    
                    Vector3 handLocation = transform.position 
                                           + transform.forward * 0.2f 
                                           + transform.right * 0.1f 
                                           + transform.up * -0.1f;
                    
                    ItemBBoxInfo itemBBoxInfo = hit.collider.GetComponent<ItemBBoxInfo>();
                    itemBBoxInfo.DeleteFrontmostItem();
                    
                    DisablePhysics(selectedItem);

                    selectedItem = Instantiate(
                        selectedItem,
                        handLocation,
                        transform.rotation,
                        transform
                    );

                    selectedItem.transform.Rotate(Vector3.up, -60);
                    selectedItem.tag = "RetailItem";

                    rightHandItem = selectedItem;
                    rightHandUsed = true;
                }
            }
        }
    }

    void Update()
    {
        // GetKeyDown must live in Update — FixedUpdate can miss single-frame events
        if (DataHandler.Instance.agentInteractionStyle == AgentInteractionStyle.Manual)
        {
            if (Input.GetKeyDown(KeyCode.Return)) ToggleGrip();
            if (Input.GetKeyDown(KeyCode.P)) TogglePoint();
        }
    }

    private void HandleMovement()
    {
        Vector3 fwd = transform.forward;
        Vector3 right = transform.right;
        float m = movementSpeed * Time.deltaTime;
        float r = rotateSpeed * Time.deltaTime;
        bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

        if (!ctrl) {
            if (Input.GetKey(KeyCode.W))
            {
                rigidbody.AddForce(fwd * m, ForceMode.Impulse);
            }
            else if (Input.GetKey(KeyCode.A))
            {
                rigidbody.AddForce(-right * m, ForceMode.Impulse);
            }
            else if (Input.GetKey(KeyCode.S))
            {
                rigidbody.AddForce(-fwd * m, ForceMode.Impulse);
            }
            else if (Input.GetKey(KeyCode.D))
            {
                rigidbody.AddForce(right * m, ForceMode.Impulse);
            }

            if (Input.GetKey(KeyCode.RightArrow))
            {
                transform.Rotate(Vector3.up, r);
            }
            else if (Input.GetKey(KeyCode.LeftArrow))
            {
                transform.Rotate(Vector3.up, -r);
            }
            else if (Input.GetKey(KeyCode.UpArrow))
            {
                transform.Rotate(Vector3.right, -r);
            }
            else if (Input.GetKey(KeyCode.DownArrow))
                transform.Rotate(Vector3.right, r);
        } 
        else
        {
            if (DataHandler.Instance.agentInteractionStyle ==
                AgentInteractionStyle.Manual)
                HandleManualHandControls();
        }

        AnimateHand();
        
        // Counteract any z-wise rotation (tilting your head right/left)
        Vector3 e = transform.eulerAngles;
        e.z = 0;
        transform.rotation = Quaternion.Euler(e);
    }

    private void AnimateHand()
    {
        if (handAnimator != null)
        {
            float gripTarget = isGripped || isPointing ? 1f : 0f;
            float triggerTarget = isPointing ? 1f : 0f;

            // When gripping, drive Trigger to 0 for a smooth pointing → grip transition
            if (isGripped) triggerTarget = 0f;

            currentGrip = Mathf.MoveTowards(currentGrip, gripTarget, gripSpeed * Time.fixedDeltaTime);
            currentTrigger = Mathf.MoveTowards(currentTrigger, triggerTarget, gripSpeed * Time.fixedDeltaTime);

            handAnimator.SetFloat("Grip", currentGrip);
            handAnimator.SetFloat("Trigger", currentTrigger);
        }
    }

    private void HandleManualHandControls()
    {
        /* CTRL + keys listed below */

        if (agentHand == null) return;

        if (_grabbedDoor != null)
        {
            // Lock hand to handle position, all movement input drives the door instead
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

        if (localPos.magnitude > handMoveRange)
            localPos = localPos.normalized * handMoveRange;

        agentHand.transform.localPosition = localPos;

        /* Manual item/door grabbing CTRL+ENTER — handled in Update() */
    }

    private void DriveDoorFromInput()
    {
        // Build a normalised input vector in agent-local space (same axes as hand movement)
        Vector3 inputLocal = Vector3.zero;
        if (Input.GetKey(KeyCode.W)) inputLocal += Vector3.forward;
        if (Input.GetKey(KeyCode.S)) inputLocal -= Vector3.forward;
        if (Input.GetKey(KeyCode.A)) inputLocal -= Vector3.right;
        if (Input.GetKey(KeyCode.D)) inputLocal += Vector3.right;
        if (Input.GetKey(KeyCode.E)) inputLocal += Vector3.up;
        if (Input.GetKey(KeyCode.Q)) inputLocal -= Vector3.up;

        if (inputLocal.sqrMagnitude < 0.001f) return;

        Vector3 inputWorld = transform.TransformDirection(inputLocal.normalized);

        HingeJoint hinge   = _grabbedDoor.Hinge;
        Rigidbody  doorRb  = _grabbedDoor.DoorRigidbody;

        Vector3 axisWorld   = doorRb.transform.TransformDirection(hinge.axis);
        Vector3 anchorWorld = doorRb.transform.TransformPoint(hinge.anchor);
        Vector3 toHandle    = _grabbedDoor.transform.position - anchorWorld;
        float   radius      = toHandle.magnitude;
        if (radius < 0.001f) return;

        // Only the tangential component of input rotates the door; radial is ignored
        Vector3 tangent           = Vector3.Cross(axisWorld, toHandle.normalized);
        float   tangentialAmount  = Vector3.Dot(inputWorld, tangent);

        _grabbedDoor.DoorBuilder.ApplyHandForce(tangent * (tangentialAmount * doorHandleForce));
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

    public bool IsHoldingItem() => rightHandUsed;

    public void TogglePoint()
    {
        isPointing = !isPointing;
        isGripped = false;
        if (_handCollisionDetector != null) _handCollisionDetector.IsPointing = isPointing;
        if (_handCollider != null)
        {
            if (isPointing)
            {
                Vector3 c = new Vector3(0.06f, -0.01f, 0.04f);
                _handCollider.center = c;
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
            isPointing = false;
            if (_handCollisionDetector != null) _handCollisionDetector.IsPointing = false;
            
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

        if (!isGripped && rightHandItem != null)
        {
            DropCurrentlyHeldItem();
        }
    }

    void DropCurrentlyHeldItem()
    {
        rightHandItem.transform.SetParent(null);
        Rigidbody rb = rightHandItem.GetComponent<Rigidbody>();
        rb.isKinematic = false;
        rb.useGravity = true;
        rb.interpolation = RigidbodyInterpolation.Extrapolate;
        BoxCollider boxCollider = rightHandItem.GetComponentInChildren<BoxCollider>();
        if (boxCollider != null) boxCollider.enabled = true;
        rightHandItem = null;
        rightHandUsed = false;
    }

    void InstantiateItemFromBBox()
    {
        string itemName = _handCollisionDetector.DetectedItem.name;
        ItemBBoxInfo itemBBoxInfo = _handCollisionDetector.DetectedItemBBoxInfo;

        var selectedItem = Resources.Load<GameObject>("Prefabs/Products/" + itemName);
        selectedItem.transform.position = Vector3.zero;

        itemBBoxInfo.DeleteFrontmostItem();
        DisablePhysics(selectedItem);

        selectedItem = Instantiate(
            selectedItem,
            agentHand.transform.position - new Vector3(0, 0.1f, 0),
            transform.rotation,
            agentHand.transform
        );

        selectedItem.transform.Rotate(Vector3.up, -60);
        selectedItem.tag = "RetailItem";

        rightHandItem = selectedItem;
        rightHandUsed = true;
    }

    void ThrowItem(GameObject item)
    {
        item.transform.SetParent(null);
        Rigidbody rb = item.GetComponent<Rigidbody>();
        rb.isKinematic = false;
        rb.useGravity = true;
        rb.interpolation = RigidbodyInterpolation.Extrapolate;
        rb.AddForce(transform.forward * throwStrength, ForceMode.Impulse);
        BoxCollider boxCollider = item.GetComponentInChildren<BoxCollider>();
        boxCollider.enabled = true;
        rightHandItem = null;
    }
    
    void DisablePhysics(GameObject item)
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
