using UnityEngine;

public class OutlineController : MonoBehaviour
{
    // Time before "unlooking"
    [SerializeField] private float lookTimeout = 0.05f; 
    private float _lastLookTime;
    private bool _isBeingLookedAt;
    private OutlineFx.OutlineFx _outlineFxScript;

    private void Start()
    {
        _outlineFxScript = GetComponent<OutlineFx.OutlineFx>();
        _outlineFxScript.enabled = false;
    }
    
    public void OnGaze()
    {
        _lastLookTime = Time.time;
        
        if (!_isBeingLookedAt)
        {
            _isBeingLookedAt = true;
            _outlineFxScript.enabled = true;
        }
    }

    private void Update()
    {
        // If the current time passes the last hit + timeout, we stopped looking
        if (_isBeingLookedAt && Time.timeAsDouble > _lastLookTime + lookTimeout)
        {
            _isBeingLookedAt = false;
            _outlineFxScript.enabled = false;
        }
    }
}
