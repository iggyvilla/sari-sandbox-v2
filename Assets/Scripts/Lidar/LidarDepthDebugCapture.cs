using UnityEngine;

public class LidarDepthDebugCapture : MonoBehaviour
{
    [SerializeField] private Camera sourceCamera;
    [SerializeField] private LidarSensor lidarSensor;
    [SerializeField] private HumanoidGhostFollower hiddenGhost;

    [ContextMenu("Capture LiDAR Depth Debug")]
    public void Capture()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning($"{nameof(LidarDepthDebugCapture)} only captures while the scene is playing.");
            return;
        }

        if (sourceCamera == null)
            sourceCamera = GetComponent<Camera>() ?? Camera.main;

        if (sourceCamera == null)
        {
            Debug.LogError($"{nameof(LidarDepthDebugCapture)} needs a source camera.");
            return;
        }

        LidarSensor activeLidarSensor = lidarSensor != null
            ? lidarSensor
            : LidarSensor.ResolveLevelSensor(sourceCamera);

        StartCoroutine(activeLidarSensor.CaptureForwardDepthDebug(
            sourceCamera,
            hiddenGhost,
            Debug.Log,
            Debug.LogError));
    }
}
