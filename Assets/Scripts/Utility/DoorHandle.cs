using UnityEngine;

public class DoorHandle : MonoBehaviour
{
    public HingeJoint Hinge { get; private set; }
    public Rigidbody DoorRigidbody { get; private set; }
    public OutlineController OutlineController { get; private set; }
    public HingedDoorBuilder DoorBuilder { get; private set; }

    void Awake()
    {
        Hinge = GetComponentInParent<HingeJoint>();
        DoorRigidbody = GetComponentInParent<Rigidbody>();
        OutlineController = GetComponent<OutlineController>();
        DoorBuilder = GetComponentInParent<HingedDoorBuilder>();
    }
}
