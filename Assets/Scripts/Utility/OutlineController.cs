using UnityEngine;

public class OutlineController : MonoBehaviour
{
    // Time before "unlooking"
    [SerializeField] private float lookTimeout = 0.05f;
    private float _lastLookTime;
    private bool _isBeingLookedAt;
    private OutlineFx.OutlineFx _outlineFxScript;

    private void Awake()
    {
        SetOutlineVisible(false);
    }

    public void OnGaze()
    {
        _lastLookTime = Time.time;

        if (!_isBeingLookedAt)
            SetOutlineVisible(true);
    }

    public void ResetOutlineState()
    {
        _lastLookTime = 0f;
        SetOutlineVisible(false);
    }

    private void Update()
    {
        // If the current time passes the last hit + timeout, we stopped looking
        if (Time.time > _lastLookTime + lookTimeout)
            SetOutlineVisible(false);
    }

    // Update only needs to run while an outline is showing.
    private void SetOutlineVisible(bool visible)
    {
        _isBeingLookedAt = visible;
        enabled = visible;
        if (_outlineFxScript == null)
            _outlineFxScript = GetComponent<OutlineFx.OutlineFx>();
        if (_outlineFxScript != null)
            _outlineFxScript.enabled = visible;
    }
}
