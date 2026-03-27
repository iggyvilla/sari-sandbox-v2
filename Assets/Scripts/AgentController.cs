using UnityEngine;
using UnityEngine.AI;

public class AgentController : MonoBehaviour
{
    [SerializeField] float movementSpeed;
    [SerializeField] float rotateSpeed;
    [SerializeField] float throwStrength;
    private Rigidbody rigidbody;
    private LayerMask interactableLayerMask;
    private GameObject rightHandItem;
    private bool rightHandUsed;
    private NavMeshAgent _agent;
    public GameObject target;
    
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        rigidbody = GetComponentInParent<Rigidbody>();
        rightHandUsed = false;
        // Only trigger items in the "Interactable" layer
        interactableLayerMask = LayerMask.GetMask("SariInteractable");
        _agent = GetComponent<NavMeshAgent>();
    }
    
    void FixedUpdate()
    {
        HandleMovement();
        
        if (Input.GetKey(KeyCode.Q) && rightHandUsed)
        {
            ThrowItem(rightHandItem);
            rightHandUsed = false;
        }
        
        RaycastHit hit;

        Debug.DrawRay(transform.position, transform.TransformDirection(Vector3.forward) * 10f, Color.yellow);
        
        if (
            Physics.Raycast(
                transform.position,
                transform.TransformDirection(Vector3.forward), 
                out hit,
                Mathf.Infinity, interactableLayerMask
            )
        )
        {
            string hitName = hit.transform.name;

            if (hit.collider.CompareTag("Wall")) return;
            
            SariUIHandler.Instance.UpdateInfoText(hitName);
            
            OutlineController outlineControllerScript = hit.collider.GetComponent<OutlineController>();
            if (outlineControllerScript)
            {
                outlineControllerScript.OnGaze();
            }
            
            // For "grabbing" items
            if (Input.GetKey(KeyCode.Return))
            {
                HingedDoorBuilder hingedDoorHandler = hit.collider.GetComponentInParent<HingedDoorBuilder>();

                if (hingedDoorHandler != null)
                {
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

                    selectedItem = Instantiate(selectedItem, handLocation,
                        transform.rotation, transform);
                    
                    selectedItem.transform.Rotate(Vector3.up, -60);
                    
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
        else if (Input.GetKey(KeyCode.DownArrow)) transform.Rotate(Vector3.right, r);
        
        // Counteract any z-wise rotation (tilting your head right/left)
        Vector3 e = transform.eulerAngles;
        e.z = 0;
        transform.rotation = Quaternion.Euler(e);
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
