using UnityEngine;

public class IKAgentController : AgentControllerBase
{
    [Header("IK Humanoid")]
    [SerializeField] Animator bodyAnimator;
    [SerializeField] Transform ikHeadJoint;
    [SerializeField] Transform ikHandColliderSource;
    [SerializeField] Transform leftIkHandColliderSource;
    [SerializeField] Transform lookAtTarget;

    public Animator BodyAnimator => bodyAnimator;
    public Transform HeadJoint => ikHeadJoint;
    public Transform HandTarget => agentHand != null ? agentHand.transform : null;
    public Transform RightHandTarget => agentHand != null ? agentHand.transform : null;
    public Transform LeftHandTarget => leftAgentHand != null ? leftAgentHand.transform : null;
    public Transform LookAtTarget => lookAtTarget;

    protected override void Start()
    {
        base.Start();
        handAnimator = bodyAnimator;
    }

    private void Update()
    {
        HandleCrouchInput();

        Vector3 pos = transform.position;
        pos.y = CurrentLocalViewHeight;
        transform.position = pos;
        
        // Manual hand input is handled in AgentControllerBase so left/right modifiers
        // can choose the target hand before Enter toggles grip.
    }

    protected override void InitializeHandComponents()
    {
        // BoxCollider and HandCollisionDetector live on a separate tracking transform,
        // not on the IK target itself.
        InitializeHandRuntime(
            AgentHandSide.Right,
            agentHand,
            bodyAnimator,
            ikHandColliderSource != null ? ikHandColliderSource.GetComponent<HandCollisionDetector>() : null,
            ikHandColliderSource != null ? ikHandColliderSource.GetComponent<BoxCollider>() : null);

        InitializeHandRuntime(
            AgentHandSide.Left,
            leftAgentHand,
            bodyAnimator,
            leftIkHandColliderSource != null ? leftIkHandColliderSource.GetComponent<HandCollisionDetector>() : null,
            leftIkHandColliderSource != null ? leftIkHandColliderSource.GetComponent<BoxCollider>() : null);
    }

    // Up/Down rotates only the head joint; Left/Right (handled in base) rotates the whole body.
    protected override void ApplyVerticalRotation(float r)
    {
        if (isMultiplayerAgent) return;
        if (ikHeadJoint == null) { base.ApplyVerticalRotation(r); return; }
        if (Input.GetKey(KeyCode.UpArrow)) ikHeadJoint.Rotate(Vector3.right, -r);
        else if (Input.GetKey(KeyCode.DownArrow)) ikHeadJoint.Rotate(Vector3.right, r);
    }

    private void LateUpdate()
    {
        if (ikHeadJoint == null) return;
        Vector3 e = ikHeadJoint.eulerAngles;
        e.y = transform.eulerAngles.y;
        e.z = 0f;
        ikHeadJoint.rotation = Quaternion.Euler(e);
    }

    private void OnAnimatorIK(int layerIndex)
    {
        if (bodyAnimator == null || lookAtTarget == null) return;
        bodyAnimator.SetLookAtPosition(lookAtTarget.position);
        // bodyAnimator.SetLookAtWeight(1f, 0.5f, 1f, 0f, 0.5f);
        bodyAnimator.SetLookAtWeight(1f);
    }

    // Add extra animator parameters here as the humanoid rig grows.
    protected override void AnimateBody()
    {
        if (bodyAnimator == null || rigidbody == null) return;

        Vector3 hVel = rigidbody.linearVelocity;
        hVel.y = 0;
        bodyAnimator.SetFloat("Speed", hVel.magnitude);

        if (isMultiplayerAgent) return;

        // Manual hand movement should only move the selected hand.
        if (IsManualHandControlActive()) return;

        bodyAnimator.SetBool("isWalking", Input.GetKey(KeyCode.W));
        bodyAnimator.SetBool("isWalkingLeft", Input.GetKey(KeyCode.A));
        bodyAnimator.SetBool("isWalkingRight", Input.GetKey(KeyCode.D));
        bodyAnimator.SetBool("isWalkingBackward", Input.GetKey(KeyCode.S));
    }
}
