using UnityEngine;

public class HumanoidGhostFollower : MonoBehaviour
{
    private static readonly int SpeedParameter = Animator.StringToHash("Speed");
    private static readonly int GripParameter = Animator.StringToHash("Grip");
    private static readonly int TriggerParameter = Animator.StringToHash("Trigger");
    private static readonly int IsWalkingParameter = Animator.StringToHash("isWalking");
    private static readonly int IsWalkingBackwardParameter = Animator.StringToHash("isWalkingBackward");
    private static readonly int IsWalkingLeftParameter = Animator.StringToHash("isWalkingLeft");
    private static readonly int IsWalkingRightParameter = Animator.StringToHash("isWalkingRight");

    private AgentControllerBase _authority;
    private Animator _bodyAnimator;
    private Transform _headJoint;
    private Transform _handTarget;
    private Transform _leftHandTarget;
    private Transform _lookAtTarget;
    private Renderer[] _renderers;
    private bool[] _rendererInitialStates;
    private Quaternion _handRotationOffset;
    private Quaternion _leftHandRotationOffset;
    private Vector3 _lastPosition;
    private int _captureSuppressionDepth;
    private bool _hasSpeedParameter;
    private bool _hasGripParameter;
    private bool _hasTriggerParameter;
    private bool _hasIsWalkingParameter;
    private bool _hasIsWalkingBackwardParameter;
    private bool _hasIsWalkingLeftParameter;
    private bool _hasIsWalkingRightParameter;

    public AgentControllerBase Authority => _authority;

    public void Bind(AgentControllerBase authority, IKAgentController humanoidController)
    {
        _authority = authority;
        _bodyAnimator = humanoidController.BodyAnimator;
        _headJoint = humanoidController.HeadJoint;
        _handTarget = humanoidController.RightHandTarget;
        _leftHandTarget = humanoidController.LeftHandTarget;
        _lookAtTarget = humanoidController.LookAtTarget;

        CacheAnimatorParameters();
        SanitizePresentationOnlyGhost();

        Transform sourceHand = _authority.HandTransform;
        if (sourceHand != null && _handTarget != null)
            _handRotationOffset = Quaternion.Inverse(sourceHand.rotation) * _handTarget.rotation;

        Transform sourceLeftHand = _authority.LeftHandTransform;
        if (sourceLeftHand != null && _leftHandTarget != null)
            _leftHandRotationOffset = Quaternion.Inverse(sourceLeftHand.rotation) * _leftHandTarget.rotation;

        FollowAuthority();
        _lastPosition = transform.position;
    }

    public void SetRenderersVisible(bool visible)
    {
        if (_renderers == null) return;

        if (!visible)
        {
            _captureSuppressionDepth++;
            foreach (Renderer bodyRenderer in _renderers)
                bodyRenderer.enabled = false;
            return;
        }

        _captureSuppressionDepth = Mathf.Max(0, _captureSuppressionDepth - 1);
        if (_captureSuppressionDepth > 0) return;

        for (int i = 0; i < _renderers.Length; i++)
            _renderers[i].enabled = _rendererInitialStates[i];
    }

    private void Update()
    {
        FollowAuthority();
        AnimateBody();
    }

    private void LateUpdate()
    {
        FollowAuthority();
    }

    private void OnAnimatorIK(int layerIndex)
    {
        if (_bodyAnimator == null || _authority == null) return;

        Transform view = _authority.ViewTransform;
        Vector3 lookAtPosition = view.position + view.forward * 10f;
        if (_lookAtTarget != null) _lookAtTarget.position = lookAtPosition;

        _bodyAnimator.SetLookAtPosition(lookAtPosition);
        _bodyAnimator.SetLookAtWeight(1f);
    }

    private void FollowAuthority()
    {
        if (_authority == null) return;

        Transform movementRoot = _authority.MovementRoot;
        Transform view = _authority.ViewTransform;
        Vector3 viewEuler = view.eulerAngles;

        transform.SetPositionAndRotation(
            movementRoot.position + Vector3.up * 0.1f,
            Quaternion.Euler(0f, viewEuler.y, 0f));

        if (_headJoint != null)
            _headJoint.rotation = Quaternion.Euler(viewEuler.x, viewEuler.y, 0f);

        Transform sourceHand = _authority.HandTransform;
        if (sourceHand != null && _handTarget != null)
            _handTarget.SetPositionAndRotation(
                sourceHand.position,
                sourceHand.rotation * _handRotationOffset);

        Transform sourceLeftHand = _authority.LeftHandTransform;
        if (sourceLeftHand != null && _leftHandTarget != null)
            _leftHandTarget.SetPositionAndRotation(
                sourceLeftHand.position,
                sourceLeftHand.rotation * _leftHandRotationOffset);
    }

    private void AnimateBody()
    {
        if (_bodyAnimator == null || _authority == null) return;

        Vector3 worldVelocity = (transform.position - _lastPosition) / Mathf.Max(Time.deltaTime, 0.0001f);
        _lastPosition = transform.position;

        Vector3 localVelocity = transform.InverseTransformDirection(worldVelocity);
        if (_hasSpeedParameter)
            _bodyAnimator.SetFloat(SpeedParameter, worldVelocity.magnitude);
        if (_hasGripParameter)
            _bodyAnimator.SetFloat(GripParameter, _authority.GripAmount);
        if (_hasTriggerParameter)
            _bodyAnimator.SetFloat(TriggerParameter, _authority.TriggerAmount);
        if (_hasIsWalkingParameter)
            _bodyAnimator.SetBool(IsWalkingParameter, localVelocity.z > 0.01f);
        if (_hasIsWalkingBackwardParameter)
            _bodyAnimator.SetBool(IsWalkingBackwardParameter, localVelocity.z < -0.01f);
        if (_hasIsWalkingLeftParameter)
            _bodyAnimator.SetBool(IsWalkingLeftParameter, localVelocity.x < -0.01f);
        if (_hasIsWalkingRightParameter)
            _bodyAnimator.SetBool(IsWalkingRightParameter, localVelocity.x > 0.01f);
    }

    private void CacheAnimatorParameters()
    {
        _hasSpeedParameter = false;
        _hasGripParameter = false;
        _hasTriggerParameter = false;
        _hasIsWalkingParameter = false;
        _hasIsWalkingBackwardParameter = false;
        _hasIsWalkingLeftParameter = false;
        _hasIsWalkingRightParameter = false;

        if (_bodyAnimator == null) return;

        foreach (AnimatorControllerParameter parameter in _bodyAnimator.parameters)
        {
            if (parameter.nameHash == SpeedParameter && parameter.type == AnimatorControllerParameterType.Float)
                _hasSpeedParameter = true;
            else if (parameter.nameHash == GripParameter && parameter.type == AnimatorControllerParameterType.Float)
                _hasGripParameter = true;
            else if (parameter.nameHash == TriggerParameter && parameter.type == AnimatorControllerParameterType.Float)
                _hasTriggerParameter = true;
            else if (parameter.nameHash == IsWalkingParameter && parameter.type == AnimatorControllerParameterType.Bool)
                _hasIsWalkingParameter = true;
            else if (parameter.nameHash == IsWalkingBackwardParameter && parameter.type == AnimatorControllerParameterType.Bool)
                _hasIsWalkingBackwardParameter = true;
            else if (parameter.nameHash == IsWalkingLeftParameter && parameter.type == AnimatorControllerParameterType.Bool)
                _hasIsWalkingLeftParameter = true;
            else if (parameter.nameHash == IsWalkingRightParameter && parameter.type == AnimatorControllerParameterType.Bool)
                _hasIsWalkingRightParameter = true;
        }
    }

    private void SanitizePresentationOnlyGhost()
    {
        foreach (AgentControllerBase controller in GetComponentsInChildren<AgentControllerBase>(true))
            controller.enabled = false;

        foreach (HandCollisionDetector detector in GetComponentsInChildren<HandCollisionDetector>(true))
            detector.enabled = false;

        foreach (Collider ghostCollider in GetComponentsInChildren<Collider>(true))
            ghostCollider.enabled = false;

        foreach (Rigidbody ghostRigidbody in GetComponentsInChildren<Rigidbody>(true))
        {
            ghostRigidbody.isKinematic = true;
            ghostRigidbody.detectCollisions = false;
        }

        foreach (Camera ghostCamera in GetComponentsInChildren<Camera>(true))
        {
            ghostCamera.enabled = false;
            ghostCamera.tag = "Untagged";
        }

        foreach (AudioListener listener in GetComponentsInChildren<AudioListener>(true))
            listener.enabled = false;

        if (_bodyAnimator != null) _bodyAnimator.applyRootMotion = false;

        _renderers = GetComponentsInChildren<Renderer>(true);
        _rendererInitialStates = new bool[_renderers.Length];
        for (int i = 0; i < _renderers.Length; i++)
            _rendererInitialStates[i] = _renderers[i].enabled;
    }
}
