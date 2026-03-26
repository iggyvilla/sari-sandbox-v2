using System;
using Unity.Mathematics.Geometry;
using UnityEngine;

public enum DoorDirection
{
    Left,
    Right
}

public class HingedDoorBuilder : MonoBehaviour
{
    private HingeJoint _hingeJoint;
    [SerializeField] private GameObject glassDoor;
    [SerializeField] private GameObject doorHandle;
    private bool _doorStatus;

    [SerializeField] private float startAngle;

    void Awake()
    {
        _hingeJoint = GetComponentInChildren<HingeJoint>();
        startAngle = transform.rotation.eulerAngles.y;
    }

    public void BuildHingeDoor(Vector3 doorDimensions, float handlePadding, DoorDirection direction)
    {
        glassDoor.transform.localScale = doorDimensions;

        float handleOffset = doorDimensions.x / 2 - handlePadding;
        
        if (direction == DoorDirection.Left)
        {
            _hingeJoint.anchor = new Vector3(doorDimensions.x/2, 0, 0);
            handleOffset = -handleOffset;
        }
        else
        {
            _hingeJoint.anchor = new Vector3(-doorDimensions.x/2, 0, 0);
            JointLimits limits = new JointLimits
            {
                min = 0,
                max = -90
            };
            _hingeJoint.limits = limits;
        }

        doorHandle.transform.position += transform.right * handleOffset;
    }

    public void ToggleDoor()
    {
        Rigidbody rb = GetComponent<Rigidbody>();

        float closeForce = 15.0f;
        
        float yDeg = transform.rotation.eulerAngles.y;
        
        // Door is closed
        
        if (yDeg <= startAngle + 5 && yDeg >= startAngle - 5)
        {
            rb.AddForce(transform.forward * closeForce);
        }
        else
        {
            rb.AddForce(-transform.forward * closeForce);
        }
        
        
    }
    
}
