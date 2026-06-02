using UnityEngine;

public class HumanoidGhostFollower : MonoBehaviour
{
    private AgentControllerBase _authority;
    private Animator _bodyAnimator;
    private Transform _headJoint;
    private Transform _handTarget;
    private Transform _lookAtTarget;
    private Renderer[] _renderers;
    private bool[] _rendererInitialStates;
    private Quaternion _handRotationOffset;
    private Vector3 _lastPosition;
    private int _captureSuppressionDepth;

    public void Bind(AgentControllerBase authority, IKAgentController humanoidController)
    {
        _authority = authority;
        _bodyAnimator = humanoidController.BodyAnimator;
        _headJoint = humanoidController.HeadJoint;
        _handTarget = humanoidController.HandTarget;
        _lookAtTarget = humanoidController.LookAtTarget;

        SanitizePresentationOnlyGhost();

        Transform sourceHand = _authority.HandTransform;
        if (sourceHand != null && _handTarget != null)
            _handRotationOffset = Quaternion.Inverse(sourceHand.rotation) * _handTarget.rotation;

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
    }

    private void AnimateBody()
    {
        if (_bodyAnimator == null || _authority == null) return;

        Vector3 worldVelocity = (transform.position - _lastPosition) / Mathf.Max(Time.deltaTime, 0.0001f);
        _lastPosition = transform.position;

        Vector3 localVelocity = transform.InverseTransformDirection(worldVelocity);
        _bodyAnimator.SetFloat("Speed", worldVelocity.magnitude);
        _bodyAnimator.SetFloat("Grip", _authority.GripAmount);
        _bodyAnimator.SetFloat("Trigger", _authority.TriggerAmount);
        _bodyAnimator.SetBool("isWalking", localVelocity.z > 0.01f);
        _bodyAnimator.SetBool("isWalkingBackward", localVelocity.z < -0.01f);
        _bodyAnimator.SetBool("isWalkingLeft", localVelocity.x < -0.01f);
        _bodyAnimator.SetBool("isWalkingRight", localVelocity.x > 0.01f);
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
