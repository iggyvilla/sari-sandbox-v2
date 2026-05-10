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
    [SerializeField] private BoxCollider doorTrigger;
    private bool _doorStatus;

    private Vector3 _closedTriggerSize;
    private Vector3 _closedTriggerCenter;

    [SerializeField] private float startAngle;

    void Awake()
    {
        _hingeJoint = GetComponentInChildren<HingeJoint>();
        startAngle = transform.rotation.eulerAngles.y;
    }

    public void BuildHingeDoor(Vector3 doorDimensions, float handlePadding, DoorDirection direction, float subShelfDepth)
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

        // doorTrigger is parented to glassDoor whose localScale == doorDimensions,
        // so divide world-space extents by that scale to get local collider values.
        Vector3 scale = glassDoor.transform.localScale;
        float colliderDepth = subShelfDepth + doorDimensions.z;
        _closedTriggerSize = new Vector3(1f, 1f, colliderDepth / scale.z);
        _closedTriggerCenter = new Vector3(0, 0, (doorDimensions.z - subShelfDepth) / 2f / scale.z);
        doorTrigger.size = _closedTriggerSize;
        doorTrigger.center = _closedTriggerCenter;
    }

    public bool IsDoorClosed()
    {
        float yDeg = transform.rotation.eulerAngles.y;
        return yDeg <= startAngle + 5 && yDeg >= startAngle - 5;
    }

    void Update()
    {
        if (IsDoorClosed())
        {
            doorTrigger.size = _closedTriggerSize;
            doorTrigger.center = _closedTriggerCenter;
        }
        else
        {
            doorTrigger.size = Vector3.one;
            doorTrigger.center = Vector3.zero;
        }
    }

    public void ApplyHandForce(Vector3 worldForce)
    {
        Rigidbody rb = GetComponent<Rigidbody>();
        rb.AddForce(worldForce);
    }

    public void ToggleDoor()
    {
        Rigidbody rb = GetComponent<Rigidbody>();

        float closeForce = 15.0f;
        
        if (IsDoorClosed())
            rb.AddForce(transform.forward * closeForce);
        else
            rb.AddForce(-transform.forward * closeForce);
    }
    
}
