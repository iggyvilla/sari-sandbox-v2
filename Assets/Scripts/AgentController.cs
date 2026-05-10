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

    private Rigidbody rigidbody;
    private LayerMask interactableLayerMask;
    private GameObject rightHandItem;
    private bool rightHandUsed;
    private Animator handAnimator;
    private float currentGrip;
    private bool isGripped;
    private HandItemDetector handItemDetector;
    
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
            handItemDetector = agentHand.GetComponent<HandItemDetector>();
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
        // _agent.SetDestination(target.transform.position);
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
            currentGrip = Mathf.MoveTowards(
                currentGrip, 
                isGripped ? 1f : 0f, 
                gripSpeed * Time.fixedDeltaTime
            );
            handAnimator.SetFloat("Grip", currentGrip);
        }
    }

    private void HandleManualHandControls()
    {
        /* CTRL + keys listed below */
        
        if (agentHand != null)
        {
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
            
            /* Manual item/door grabbing CTRL+ENTER */
            if (Input.GetKeyDown(KeyCode.Return)) ToggleGrip();
        }
        
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

    public void ToggleGrip()
    {
        if (!isGripped)
        {
            if (agentHand != null &&
                handItemDetector != null &&
                handItemDetector.DetectedItem != null &&
                handItemDetector.DetectedItemBBoxInfo != null)
            {
                string itemName = handItemDetector.DetectedItem.name;
                ItemBBoxInfo itemBBoxInfo = handItemDetector.DetectedItemBBoxInfo;

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
            isGripped = true;
        }
        else
        {
            isGripped = false;
        }
        
        if (!isGripped && rightHandItem != null)
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
