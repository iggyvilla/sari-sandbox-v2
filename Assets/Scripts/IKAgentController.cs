using UnityEngine;

public class IKAgentController : AgentControllerBase
{
    [Header("IK Humanoid")]
    [SerializeField] Animator bodyAnimator;
    [SerializeField] Transform ikHeadJoint;
    [SerializeField] Transform ikHandColliderSource;
    [SerializeField] Transform leftIkHandColliderSource;
    [SerializeField] Transform lookAtTarget;

    private Vector3 _lastBodyPosition;

    public Animator BodyAnimator => bodyAnimator;
    public Transform HeadJoint => ikHeadJoint;
    public Transform RightHandTarget => agentHand != null ? agentHand.transform : null;
    public Transform LeftHandTarget => leftAgentHand != null ? leftAgentHand.transform : null;
    public Transform LookAtTarget => lookAtTarget;

    protected override void Start()
    {
        base.Start();
        if (rigidbody != null) _lastBodyPosition = rigidbody.position;
    }

    protected override void Update()
    {
        base.Update();

        Vector3 pos = transform.position;
        pos.y = CurrentLocalViewHeight;
        transform.position = pos;
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
        bodyAnimator.SetLookAtWeight(1f);
    }

    // Add extra animator parameters here as the humanoid rig grows.
    protected override void AnimateBody()
    {
        if (bodyAnimator == null || rigidbody == null) return;

        // TranslateAgent zeroes velocity and teleports, so derive speed from the per-step displacement.
        Vector3 bodyPosition = rigidbody.position;
        Vector3 delta = bodyPosition - _lastBodyPosition;
        _lastBodyPosition = bodyPosition;
        delta.y = 0f;
        bodyAnimator.SetFloat(AgentAnimatorParams.Speed, delta.magnitude / Time.fixedDeltaTime);

        if (isMultiplayerAgent) return;

        // Manual hand movement should only move the selected hand.
        if (IsManualHandControlActive()) return;

        bodyAnimator.SetBool(AgentAnimatorParams.IsWalking, Input.GetKey(KeyCode.W));
        bodyAnimator.SetBool(AgentAnimatorParams.IsWalkingLeft, Input.GetKey(KeyCode.A));
        bodyAnimator.SetBool(AgentAnimatorParams.IsWalkingRight, Input.GetKey(KeyCode.D));
        bodyAnimator.SetBool(AgentAnimatorParams.IsWalkingBackward, Input.GetKey(KeyCode.S));
    }
}
