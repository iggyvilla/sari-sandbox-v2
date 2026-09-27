using UnityEngine;

public class LidarDepthDebugCapture : MonoBehaviour
{
    [SerializeField] private Camera sourceCamera;
    [SerializeField] private LidarSensor lidarSensor;
    [SerializeField] private HumanoidGhostFollower hiddenGhost;

    /// <summary>
    /// Runs the LiDAR diagnostic capture from the Inspector context menu during play mode.
    /// If no sensor is assigned, this uses the same level sensor resolver as WebSocket scans.
    /// </summary>
    [ContextMenu("Capture LiDAR Depth Debug")]
    public void Capture()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning($"{nameof(LidarDepthDebugCapture)} only captures while the scene is playing.");
            return;
        }

        // Explicit checks: `??` skips Unity's destroyed/missing-object null semantics.
        if (sourceCamera == null && !TryGetComponent(out sourceCamera))
            sourceCamera = Camera.main;

        if (sourceCamera == null)
        {
            Debug.LogError($"{nameof(LidarDepthDebugCapture)} needs a source camera.");
            return;
        }

        LidarSensor activeLidarSensor = lidarSensor != null
            ? lidarSensor
            : LidarSensor.ResolveLevelSensor(sourceCamera);

        // Run on the sensor so the capture (and its cleanup) is not tied to this component's lifetime.
        activeLidarSensor.StartCoroutine(activeLidarSensor.CaptureForwardDepthDebug(
            sourceCamera,
            hiddenGhost,
            Debug.Log,
            Debug.LogError));
    }
}
