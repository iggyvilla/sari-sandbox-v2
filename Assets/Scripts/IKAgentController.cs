using UnityEngine;

public class IKAgentController : AgentControllerBase
{
    [Header("IK Humanoid")]
    [SerializeField] Animator bodyAnimator;
    [SerializeField] Transform ikHeadJoint;
    [SerializeField] Transform ikHandColliderSource;

    protected override void InitializeHandComponents()
    {
        if (agentHand == null) return;

        handAnimator = bodyAnimator;
        _handCollisionDetector = agentHand.GetComponent<HandCollisionDetector>();
        _initialHandLocalPosition = agentHand.transform.localPosition;
        _initialHandLocalRotation = agentHand.transform.localRotation;

        // BoxCollider and HandCollisionDetector live on a separate tracking transform,
        // not on the IK target itself.
        if (ikHandColliderSource != null)
        {
            _handCollider = ikHandColliderSource.GetComponent<BoxCollider>();
            _handCollisionDetector = ikHandColliderSource.GetComponent<HandCollisionDetector>();
            if (_handCollider != null)
            {
                _defaultColliderSize = _handCollider.size;
                _defaultColliderCenter = _handCollider.center;
            }
        }
    }

    // Up/Down rotates only the head joint; Left/Right (handled in base) rotates the whole body.
    protected override void ApplyVerticalRotation(float r)
    {
        if (ikHeadJoint == null) { base.ApplyVerticalRotation(r); return; }
        if (Input.GetKey(KeyCode.UpArrow)) ikHeadJoint.Rotate(Vector3.right, -r);
        else if (Input.GetKey(KeyCode.DownArrow)) ikHeadJoint.Rotate(Vector3.right, r);
    }

    // Add extra animator parameters here as the humanoid rig grows.
    protected override void AnimateBody()
    {
        if (bodyAnimator == null) return;
        Vector3 hVel = rigidbody.linearVelocity;
        hVel.y = 0;
        bodyAnimator.SetFloat("Speed", hVel.magnitude);
    }
}
