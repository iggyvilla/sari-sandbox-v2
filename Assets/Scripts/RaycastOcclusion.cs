using UnityEngine;

public class RaycastOcclusion : MonoBehaviour
{

    public GameObject camera;
    private LayerMask wallMask;
    private bool wasActive = true;
    private Renderer rend;
    
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        wallMask = LayerMask.GetMask("Wall");
        rend = GetComponent<Renderer>();
    }

    // Update is called once per frame
    void Update()
    {
        RaycastHit hit;

        Vector3 origin = transform.position;
        Vector3 direction = (camera.transform.position - origin).normalized;
        
        Debug.DrawRay(origin, direction,  Color.red);
        
        if (!rend.isVisible)
        {
            Debug.Log("Hit");
            if (wasActive)
            {
                rend.enabled = false;
                wasActive = false;
            };
        }
        else
        {
            Debug.Log("No Hit");
            if (!wasActive)
            {
                rend.enabled = true;
                wasActive = true;
            }
        }
    }
}
