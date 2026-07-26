using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

public class LidarSensor : MonoBehaviour
{
    public enum SweepMode
    {
        Full360,
        EgocentricView
    }

    private const string Magic = "LDR1";
    private const string LevelSensorMountName = "Level LiDAR Sensor";
    private const int FaceCount = 6;
    private const int DebugSampleLimit = 8;
    private const float HiddenGhostFallbackMaxDistance = 4f;
    private const float DebugProbeAzimuthDeg = 0f;
    private const float DebugProbeVerticalDeg = 0f;
    private const float RangeMissTolerance = 0.001f;
    private const float PlanarForwardEpsilonSqr = 0.000001f;

    [SerializeField] private SweepMode sweepMode = SweepMode.EgocentricView;
    [SerializeField] private int channels = 32;
    [SerializeField] private int azimuthSamples = 1024;
    [SerializeField] private int faceResolution = 512;
    [SerializeField] private float minRange = 0.1f;
    [SerializeField] private float maxRange = 20f;
    [SerializeField] private float verticalFovMinDeg = -75f;
    [SerializeField] private float verticalFovMaxDeg = 75f;
    [SerializeField] private float[] verticalAnglesDeg;
    [SerializeField] private ComputeShader depthSampler;
    [SerializeField] private Shader linearDepthShader;
    [SerializeField] private bool drawDebugProbeRaycastGizmo = true;
    [SerializeField] private float debugProbeRaycastGizmoRadius = 0.08f;
    [SerializeField] private Color debugProbeRaycastHitColor = new Color(1f, 0.8f, 0.05f, 1f);
    [SerializeField] private Color debugProbeRayColor = new Color(0.05f, 0.85f, 1f, 1f);

    private readonly Camera[] _faceCameras = new Camera[FaceCount];
    private readonly RenderTexture[] _colorTargets = new RenderTexture[FaceCount];
    private readonly RenderTexture[] _depthTargets = new RenderTexture[FaceCount];
    private readonly Quaternion[] _faceLocalRotations =
    {
        Quaternion.LookRotation(Vector3.right, Vector3.up),
        Quaternion.LookRotation(Vector3.left, Vector3.up),
        Quaternion.LookRotation(Vector3.up, Vector3.back),
        Quaternion.LookRotation(Vector3.down, Vector3.forward),
        Quaternion.LookRotation(Vector3.forward, Vector3.up),
        Quaternion.LookRotation(Vector3.back, Vector3.up)
    };

    private ComputeBuffer _verticalAnglesBuffer;
    private ComputeBuffer _rangeBuffer;
    private Material _linearDepthMaterial;
    private Camera _sourceCamera;
    private AgentControllerBase _sourceAgent;
    private Transform _sourceSelfRoot;
    private Transform _excludedHiddenGhostRoot;
    private int _rangeBufferCount;
    private int _sequence;
    private bool _isBusy;
    private bool _hasDebugProbeRaycastGizmo;
    private Vector3 _debugProbeRayOrigin;
    private Vector3 _debugProbeRayEnd;
    private Vector3 _debugProbeRaycastHitPoint;

    public bool IsBusy => _isBusy;

    public struct CenterSample
    {
        public float distance;
        public bool hit;
        public float minRange;
        public float maxRange;
        public float pitchDeg;
        public float cameraHeight;
    }

    /// <summary>
    /// Finds or creates the navigation LiDAR for an agent camera.
    /// Agent scans use a child mount under the movement root so the scan starts at head height
    /// but stays level with the floor. Full-360 scans use root yaw; egocentric scans use camera yaw.
    /// Non-agent cameras fall back to the older camera-attached behavior.
    /// </summary>
    public static LidarSensor ResolveLevelSensor(Camera sourceCamera)
    {
        if (sourceCamera == null) return null;

        AgentControllerBase agent = sourceCamera.GetComponentInParent<AgentControllerBase>();
        Transform mountRoot = agent != null ? agent.MovementRoot : null;
        if (mountRoot == null)
            return ResolveCameraSensor(sourceCamera);

        Transform mount = mountRoot.Find(LevelSensorMountName);
        if (mount == null)
        {
            GameObject mountObject = new GameObject(LevelSensorMountName);
            mount = mountObject.transform;
            mount.SetParent(mountRoot, false);
        }

        LidarSensor sensor = mount.GetComponent<LidarSensor>();
        if (sensor == null)
            sensor = mount.gameObject.AddComponent<LidarSensor>();

        sensor.AlignLevelToSource(sourceCamera, mountRoot);
        return sensor;
    }

    /// <summary>
    /// Captures one configured LiDAR scan, reads the GPU range buffer back to CPU memory,
    /// and returns the binary WebSocket payload through <paramref name="onComplete"/>.
    /// </summary>
    public IEnumerator CaptureScan(
        Camera sourceCamera,
        HumanoidGhostFollower hiddenGhost,
        Action<byte[]> onComplete,
        Action<string> onError)
    {
        if (_isBusy)
        {
            onError?.Invoke("Error: LiDAR scan already in progress");
            yield break;
        }

        _isBusy = true;
        HumanoidGhostFollower activeHiddenGhost = hiddenGhost;
        _excludedHiddenGhostRoot = null;

        try
        {
            if (!EnsureResources(sourceCamera, out string error))
            {
                onError?.Invoke(error);
                yield break;
            }

            float[] angles = GetVerticalAngles();
            UploadVerticalAngles(angles);
            EnsureRangeBuffer();
            AzimuthSweep azimuthSweep = ResolveAzimuthSweep();

            activeHiddenGhost = ResolveHiddenGhost(sourceCamera, hiddenGhost);
            _excludedHiddenGhostRoot = activeHiddenGhost != null ? activeHiddenGhost.transform : null;

            GPUInstanceTracker tracker = GPUInstanceTracker.Instance;
            tracker?.CullForLidarRange(transform.position, maxRange);

            for (int face = 0; face < FaceCount; face++)
                RenderFace(face, tracker);

            DispatchRangeSampler(azimuthSweep);

            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(_rangeBuffer);
            yield return new WaitUntil(() => request.done);

            if (request.hasError)
            {
                onError?.Invoke("Error: LiDAR GPU readback failed");
                yield break;
            }

            NativeArray<float> ranges = request.GetData<float>();
            onComplete?.Invoke(BuildPayload(angles, ranges, azimuthSweep));
        }
        finally
        {
            _excludedHiddenGhostRoot = null;
            _isBusy = false;
        }
    }

    /// <summary>
    /// Renders the forward LiDAR depth face along the source camera's center gaze ray and reads
    /// the single center pixel. The regular LiDAR scan remains level; this capture follows the
    /// camera's pitch and yaw only for the duration of this sample. Camera roll cannot change the
    /// center ray and therefore requires no separate correction.
    /// </summary>
    public IEnumerator CaptureCenterSample(
        Camera sourceCamera,
        HumanoidGhostFollower hiddenGhost,
        Action<CenterSample> onComplete,
        Action<string> onError)
    {
        if (_isBusy)
        {
            onError?.Invoke("Error: LiDAR scan already in progress");
            yield break;
        }

        _isBusy = true;
        _excludedHiddenGhostRoot = null;

        try
        {
            if (!EnsureResources(sourceCamera, out string error))
            {
                onError?.Invoke(error);
                yield break;
            }

            HumanoidGhostFollower activeHiddenGhost = ResolveHiddenGhost(sourceCamera, hiddenGhost);
            _excludedHiddenGhostRoot = activeHiddenGhost != null ? activeHiddenGhost.transform : null;

            GPUInstanceTracker tracker = GPUInstanceTracker.Instance;
            tracker?.CullForLidarRange(transform.position, maxRange);

            // Face 4 is the forward cube face. Giving it the source camera rotation makes its
            // optical center match sourceCamera.transform.forward exactly. Roll only rotates the
            // surrounding image and has no effect on the center pixel's ray.
            RenderFace(4, tracker, captureRotation: sourceCamera.transform.rotation);

            Camera centerCamera = _faceCameras[4];
            Ray centerRay = centerCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            Vector3 direction = centerRay.direction;
            float pitchDeg = Mathf.Atan2(
                -direction.y,
                Mathf.Sqrt(direction.x * direction.x + direction.z * direction.z)) * Mathf.Rad2Deg;
            float cameraHeight = centerRay.origin.y;

            int centerPixel = faceResolution / 2;
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(
                _depthTargets[4],
                0,
                centerPixel,
                1,
                centerPixel,
                1,
                0,
                1);
            yield return new WaitUntil(() => request.done);

            if (request.hasError)
            {
                onError?.Invoke("Error: LiDAR center GPU readback failed");
                yield break;
            }

            NativeArray<float> depthData = request.GetData<float>();
            if (depthData.Length == 0)
            {
                onError?.Invoke("Error: LiDAR center GPU readback returned no data");
                yield break;
            }

            float distance = depthData[0];
            bool valid =
                !float.IsNaN(distance) &&
                !float.IsInfinity(distance) &&
                distance >= minRange &&
                distance <= maxRange;
            bool hit = valid && distance < maxRange - RangeMissTolerance;

            if (!hit)
                distance = maxRange;

            onComplete?.Invoke(new CenterSample
            {
                distance = distance,
                hit = hit,
                minRange = minRange,
                maxRange = maxRange,
                pitchDeg = IsFinite(pitchDeg) ? pitchDeg : 0f,
                cameraHeight = IsFinite(cameraHeight) ? cameraHeight : 0f
            });
        }
        finally
        {
            _excludedHiddenGhostRoot = null;
            _isBusy = false;
        }
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>
    /// Fallback resolver for non-agent cameras: reuse an existing camera/parent/child sensor,
    /// or attach one directly to the camera if none exists.
    /// </summary>
    private static LidarSensor ResolveCameraSensor(Camera sourceCamera)
    {
        LidarSensor sensor =
            sourceCamera.GetComponent<LidarSensor>() ??
            sourceCamera.GetComponentInParent<LidarSensor>() ??
            sourceCamera.GetComponentInChildren<LidarSensor>(true);

        if (sensor == null)
            sensor = sourceCamera.gameObject.AddComponent<LidarSensor>();

        return sensor;
    }

    /// <summary>
    /// Places the level sensor at the camera's current world position while removing camera pitch/roll.
    /// Egocentric scans follow the camera's planar heading without leaking head tilt into the scan frame.
    /// </summary>
    private void AlignLevelToSource(Camera sourceCamera, Transform mountRoot)
    {
        transform.SetPositionAndRotation(
            sourceCamera.transform.position,
            ResolveLevelRotation(sourceCamera, mountRoot));
    }

    private Quaternion ResolveLevelRotation(Camera sourceCamera, Transform mountRoot)
    {
        if (sweepMode == SweepMode.EgocentricView && TryGetPlanarCameraForward(sourceCamera, out Vector3 planarForward))
            return Quaternion.LookRotation(planarForward, Vector3.up);

        return Quaternion.Euler(0f, mountRoot.eulerAngles.y, 0f);
    }

    private static bool TryGetPlanarCameraForward(Camera sourceCamera, out Vector3 planarForward)
    {
        planarForward = Vector3.zero;
        if (sourceCamera == null) return false;

        planarForward = Vector3.ProjectOnPlane(sourceCamera.transform.forward, Vector3.up);
        if (planarForward.sqrMagnitude <= PlanarForwardEpsilonSqr)
            return false;

        planarForward.Normalize();
        return true;
    }

    /// <summary>
    /// Captures PNG/debug artifacts for the forward LiDAR face and logs intermediate render statistics.
    /// This is intended for visual diagnosis, not for WebSocket payload generation.
    /// </summary>
    public IEnumerator CaptureForwardDepthDebug(
        Camera sourceCamera,
        HumanoidGhostFollower hiddenGhost,
        Action<string> onComplete,
        Action<string> onError)
    {
        if (_isBusy)
        {
            onError?.Invoke("Error: LiDAR scan already in progress");
            yield break;
        }

        _isBusy = true;

        RenderTexture debugTexture = null;
        ComputeBuffer depthStatsBuffer = null;
        HumanoidGhostFollower activeHiddenGhost = hiddenGhost;
        _excludedHiddenGhostRoot = null;

        try
        {
            if (!EnsureResources(sourceCamera, out string error))
            {
                onError?.Invoke(error);
                yield break;
            }
            activeHiddenGhost = ResolveHiddenGhost(sourceCamera, hiddenGhost);

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            string directory = Path.Combine(Application.persistentDataPath, "LidarDebug");
            Directory.CreateDirectory(directory);

            Debug.Log(
                "LiDAR debug step 0: " +
                $"sourceCamera={sourceCamera.name}, " +
                $"cameraPos={sourceCamera.transform.position}, " +
                $"cameraRot={sourceCamera.transform.rotation.eulerAngles}, " +
                $"sensorObject={name}, " +
                $"sensorPos={transform.position}, " +
                $"sensorRot={transform.rotation.eulerAngles}, " +
                $"selfRoot={(_sourceSelfRoot != null ? _sourceSelfRoot.name : "none")}, " +
                $"cullingMask={sourceCamera.cullingMask}, " +
                $"near={_faceCameras[4].nearClipPlane}, far={_faceCameras[4].farClipPlane}, " +
                $"reversedZ={SystemInfo.usesReversedZBuffer}");

            _excludedHiddenGhostRoot = activeHiddenGhost != null ? activeHiddenGhost.transform : null;
            Debug.Log(
                "LiDAR debug self suppression: " +
                $"requestedHiddenGhost={(hiddenGhost != null ? hiddenGhost.name : "none")}, " +
                $"resolvedHiddenGhost={(activeHiddenGhost != null ? activeHiddenGhost.name : "none")}, " +
                $"excludedSelfRoot={(_sourceSelfRoot != null ? _sourceSelfRoot.name : "none")}, " +
                $"excludedGhostRoot={(_excludedHiddenGhostRoot != null ? _excludedHiddenGhostRoot.name : "none")}, " +
                "rendererToggling=False");

            Debug.Log("LiDAR debug step 1: rendering normal color targetTexture for all six faces.");
            string colorOnlySummary = "";
            for (int face = 0; face < FaceCount; face++)
            {
                RenderFaceColorOnly(face);
                string colorOnlyPath = Path.Combine(directory, $"{timestamp}_step1_color_target_{GetFaceName(face)}.png");
                TextureStats colorOnlyStats = SaveRenderTexturePng(_colorTargets[face], colorOnlyPath);
                colorOnlySummary +=
                    $"face {face} {GetFaceName(face)} targetTextureColor " +
                    $"nonBlack={colorOnlyStats.nonBlackPixels}/{colorOnlyStats.totalPixels}, " +
                    $"min={colorOnlyStats.minValue}, max={colorOnlyStats.maxValue}, file={colorOnlyPath}\n";
            }
            Debug.Log("LiDAR debug step 1 result:\n" + colorOnlySummary);

            GPUInstanceTracker tracker = GPUInstanceTracker.Instance;
            Debug.Log(
                "LiDAR debug step 2: " +
                (tracker != null
                    ? "GPUInstanceTracker found; running range cull and linear-depth render path."
                    : "GPUInstanceTracker not found; linear-depth path will only include normal scene renderers."));
            tracker?.CullForLidarRange(transform.position, maxRange);

            debugTexture = new RenderTexture(faceResolution, faceResolution, 0, RenderTextureFormat.ARGB32)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            debugTexture.Create();

            RenderFace(4, null, true, false, true, "step2 scene-only");
            WriteLinearDepthPreview(4, debugTexture);

            string sceneDepthPath = Path.Combine(directory, $"{timestamp}_step2_scene_linear_depth_forward.png");
            TextureStats sceneDepthImageStats = SaveRenderTexturePng(debugTexture, sceneDepthPath);
            Debug.Log(
                "LiDAR debug step 2 result: " +
                $"scene-only linear-depth visualization saved to {sceneDepthPath}; " +
                $"nonBlackPixels={sceneDepthImageStats.nonBlackPixels}/{sceneDepthImageStats.totalPixels}, " +
                $"min={sceneDepthImageStats.minValue}, max={sceneDepthImageStats.maxValue}");

            RenderFace(4, tracker, false, true, true, "step3 indirect-only");
            WriteLinearDepthPreview(4, debugTexture);

            string indirectDepthPath = Path.Combine(directory, $"{timestamp}_step3_indirect_linear_depth_forward.png");
            TextureStats indirectDepthImageStats = SaveRenderTexturePng(debugTexture, indirectDepthPath);
            Debug.Log(
                "LiDAR debug step 3 result: " +
                $"indirect-only linear-depth visualization saved to {indirectDepthPath}; " +
                $"nonBlackPixels={indirectDepthImageStats.nonBlackPixels}/{indirectDepthImageStats.totalPixels}, " +
                $"min={indirectDepthImageStats.minValue}, max={indirectDepthImageStats.maxValue}");

            RenderFace(4, tracker, true, true, true, "step4 combined");
            WriteLinearDepthPreview(4, debugTexture);

            string combinedDepthPath = Path.Combine(directory, $"{timestamp}_step4_combined_linear_depth_forward.png");
            TextureStats combinedDepthImageStats = SaveRenderTexturePng(debugTexture, combinedDepthPath);
            Debug.Log(
                "LiDAR debug step 4 result: " +
                $"scene+indirect linear-depth visualization saved to {combinedDepthPath}; " +
                $"nonBlackPixels={combinedDepthImageStats.nonBlackPixels}/{combinedDepthImageStats.totalPixels}, " +
                $"min={combinedDepthImageStats.minValue}, max={combinedDepthImageStats.maxValue}");

            depthStatsBuffer = new ComputeBuffer(4, sizeof(uint));
            depthStatsBuffer.SetData(new uint[] { 0u, 1000000u, 0u, 0u });

            int statsKernel = depthSampler.FindKernel("DepthStats");
            depthSampler.SetInt("_FaceResolution", faceResolution);
            depthSampler.SetFloat("_MaxRange", maxRange);
            depthSampler.SetVector("_ZBufferParamsForLidar", BuildZBufferParams(_faceCameras[4]));
            depthSampler.SetInt("_UsesReversedZBuffer", SystemInfo.usesReversedZBuffer ? 1 : 0);
            depthSampler.SetTexture(statsKernel, "_DebugDepth", _depthTargets[4]);
            depthSampler.SetBuffer(statsKernel, "_DepthStats", depthStatsBuffer);
            depthSampler.Dispatch(statsKernel, Mathf.CeilToInt(faceResolution / 8f), Mathf.CeilToInt(faceResolution / 8f), 1);

            AsyncGPUReadbackRequest depthStatsRequest = AsyncGPUReadback.Request(depthStatsBuffer);
            yield return new WaitUntil(() => depthStatsRequest.done);

            string depthStatsMessage;
            if (depthStatsRequest.hasError)
            {
                depthStatsMessage = "LiDAR debug step 5 result: GPU readback failed for linear depth stats.";
            }
            else
            {
                NativeArray<uint> stats = depthStatsRequest.GetData<uint>();
                depthStatsMessage =
                    "LiDAR debug step 5 result: " +
                    $"linearDepthHitPixels={stats[0]}/{faceResolution * faceResolution}, " +
                    $"linearDepthMin01={stats[1] / 1000000f:0.000000}, " +
                    $"linearDepthMax01={stats[2] / 1000000f:0.000000}, " +
                    $"linearDepthCenter01={stats[3] / 1000000f:0.000000}";
            }
            Debug.Log(depthStatsMessage);

            for (int face = 0; face < FaceCount; face++)
            {
                if (face == 4) continue;
                RenderFace(face, tracker);
            }

            float[] angles = GetVerticalAngles();
            UploadVerticalAngles(angles);
            EnsureRangeBuffer();
            AzimuthSweep azimuthSweep = ResolveAzimuthSweep();
            DispatchRangeSampler(azimuthSweep);

            AsyncGPUReadbackRequest rangeRequest = AsyncGPUReadback.Request(_rangeBuffer);
            yield return new WaitUntil(() => rangeRequest.done);

            string rangeMessage;
            string probeMessage = "LiDAR debug step 7 probe: skipped because range buffer readback failed.";
            if (rangeRequest.hasError)
            {
                rangeMessage = "LiDAR debug step 6 result: GPU readback failed for range buffer.";
            }
            else
            {
                NativeArray<float> ranges = rangeRequest.GetData<float>();
                RangeStats rangeStats = CalculateRangeStats(ranges);
                rangeMessage =
                    "LiDAR debug step 6 result: " +
                    $"rangeHits={rangeStats.hitCount}/{rangeStats.totalCount}, " +
                    $"maxRangeCount={rangeStats.maxRangeCount}, " +
                    $"minRange={rangeStats.minRange:0.000}, " +
                    $"maxHitRange={rangeStats.maxHitRange:0.000}";
                LidarProbeSample probe = BuildLidarProbeSample(
                    angles,
                    ranges,
                    azimuthSweep,
                    DebugProbeAzimuthDeg,
                    DebugProbeVerticalDeg);
                probeMessage = FormatLidarProbeMessage(probe);
            }
            Debug.Log(rangeMessage);
            Debug.Log(probeMessage);

            string message =
                "LiDAR debug complete.\n" +
                "Step 1 targetTexture color PNGs:\n" + colorOnlySummary +
                $"Step 2 scene-only linear-depth PNG: {sceneDepthPath}\n" +
                $"Step 3 indirect-only linear-depth PNG: {indirectDepthPath}\n" +
                $"Step 4 scene+indirect linear-depth PNG: {combinedDepthPath}\n" +
                depthStatsMessage + "\n" +
                rangeMessage + "\n" +
                probeMessage;
            Debug.Log(message);
            onComplete?.Invoke(message);
        }
        finally
        {
            _excludedHiddenGhostRoot = null;
            if (debugTexture != null) Destroy(debugTexture);
            depthStatsBuffer?.Release();
            _isBusy = false;
        }
    }

    /// <summary>
    /// Loads shaders, creates hidden face cameras/render targets, clamps scan settings,
    /// and records the source camera/agent roots used for self-culling.
    /// </summary>
    private bool EnsureResources(Camera sourceCamera, out string error)
    {
        error = null;

        if (sourceCamera == null)
        {
            error = "Error: no camera found for LiDAR scan";
            return false;
        }
        _sourceCamera = sourceCamera;
        _sourceAgent = sourceCamera.GetComponentInParent<AgentControllerBase>();
        _sourceSelfRoot = ResolveSelfRoot(sourceCamera);

        if (depthSampler == null)
            depthSampler = Resources.Load<ComputeShader>("LidarDepthSampler");

        if (depthSampler == null)
        {
            error = "Error: missing LidarDepthSampler compute shader";
            return false;
        }

        if (linearDepthShader == null)
            linearDepthShader = Resources.Load<Shader>("LidarLinearDepth");

        if (linearDepthShader == null)
        {
            error = "Error: missing LidarLinearDepth shader";
            return false;
        }

        if (_linearDepthMaterial == null)
        {
            _linearDepthMaterial = new Material(linearDepthShader)
            {
                enableInstancing = true
            };
        }

        channels = Mathf.Max(1, channels);
        azimuthSamples = Mathf.Max(1, azimuthSamples);
        faceResolution = Mathf.Max(16, faceResolution);
        minRange = Mathf.Max(0.01f, minRange);
        maxRange = Mathf.Max(minRange + 0.01f, maxRange);

        for (int face = 0; face < FaceCount; face++)
        {
            if (_faceCameras[face] == null)
                _faceCameras[face] = CreateFaceCamera(face);

            ConfigureFaceCamera(_faceCameras[face], sourceCamera);
            EnsureRenderTargets(face);
        }

        return true;
    }

    /// <summary>
    /// Creates one hidden 90-degree camera for a cube-map face of the LiDAR depth capture.
    /// </summary>
    private Camera CreateFaceCamera(int face)
    {
        GameObject cameraObject = new GameObject($"LiDAR Face {face}");
        cameraObject.hideFlags = HideFlags.HideAndDontSave;
        cameraObject.transform.SetParent(transform, false);

        Camera camera = cameraObject.AddComponent<Camera>();
        camera.enabled = false;
        camera.fieldOfView = 90f;
        camera.aspect = 1f;
        camera.allowHDR = false;
        camera.allowMSAA = false;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        camera.depthTextureMode = DepthTextureMode.Depth;
        return camera;
    }

    /// <summary>
    /// Copies render settings from the source camera, then forces LiDAR-specific depth range,
    /// square projection, and render-texture behavior.
    /// </summary>
    private void ConfigureFaceCamera(Camera faceCamera, Camera sourceCamera)
    {
        faceCamera.CopyFrom(sourceCamera);
        faceCamera.enabled = false;
        faceCamera.fieldOfView = 90f;
        faceCamera.aspect = 1f;
        faceCamera.nearClipPlane = Mathf.Max(0.01f, minRange);
        faceCamera.farClipPlane = maxRange;
        faceCamera.allowHDR = false;
        faceCamera.allowMSAA = false;
        faceCamera.clearFlags = CameraClearFlags.SolidColor;
        faceCamera.backgroundColor = Color.black;
        faceCamera.depthTextureMode = DepthTextureMode.Depth;
        faceCamera.stereoTargetEye = StereoTargetEyeMask.None;
        faceCamera.cameraType = CameraType.Game;
        faceCamera.forceIntoRenderTexture = true;
    }

    /// <summary>
    /// Ensures each cube face has a color target for debugging and an RFloat target for linear depth.
    /// </summary>
    private void EnsureRenderTargets(int face)
    {
        if (_colorTargets[face] == null || _colorTargets[face].width != faceResolution || _colorTargets[face].depth != 24)
        {
            ReleaseRenderTexture(_colorTargets[face]);
            _colorTargets[face] = new RenderTexture(faceResolution, faceResolution, 24, RenderTextureFormat.ARGB32)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            _colorTargets[face].Create();
        }

        if (_depthTargets[face] == null || _depthTargets[face].width != faceResolution || _depthTargets[face].format != RenderTextureFormat.RFloat)
        {
            ReleaseRenderTexture(_depthTargets[face]);
            _depthTargets[face] = new RenderTexture(faceResolution, faceResolution, 24, RenderTextureFormat.RFloat)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            _depthTargets[face].Create();
        }
    }

    /// <summary>
    /// Renders one cube face into the RFloat linear-depth target, including regular scene renderers
    /// and GPU-instanced/indirect objects that need explicit draw commands.
    /// </summary>
    private void RenderFace(
        int face,
        GPUInstanceTracker tracker,
        bool drawScene = true,
        bool drawIndirect = true,
        bool logForwardStats = false,
        string passLabel = null,
        Quaternion? captureRotation = null)
    {
        Camera faceCamera = _faceCameras[face];
        faceCamera.transform.SetPositionAndRotation(
            transform.position,
            (captureRotation ?? transform.rotation) * _faceLocalRotations[face]);

        CommandBuffer cmd = CommandBufferPool.Get("LiDAR Indirect Depth Draws");
        cmd.SetRenderTarget(_depthTargets[face]);
        cmd.SetViewport(new Rect(0, 0, faceResolution, faceResolution));
        cmd.ClearRenderTarget(true, true, new Color(maxRange, 0f, 0f, 1f));
        cmd.SetViewProjectionMatrices(
            faceCamera.worldToCameraMatrix,
            GL.GetGPUProjectionMatrix(faceCamera.projectionMatrix, false));

        LidarDrawStats sceneStats = drawScene
            ? DrawSceneRenderersForFace(cmd, faceCamera, logForwardStats && face == 4)
            : LidarDrawStats.Empty;

        LidarIndirectDrawStats indirectStats =
            drawIndirect && tracker != null
                ? tracker.AddLidarDepthDrawCommands(cmd, _linearDepthMaterial)
                : new LidarIndirectDrawStats();

        Graphics.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);

        if (logForwardStats && face == 4)
        {
            Debug.Log(
                $"LiDAR depth face forward draw stats ({passLabel ?? "unnamed"}): " +
                $"drawScene={drawScene}, drawIndirect={drawIndirect}, " +
                FormatSceneStats(sceneStats) + ", " +
                FormatIndirectStats(indirectStats));
        }
    }

    /// <summary>
    /// Adds standard MeshRenderer/SkinnedMeshRenderer depth draw commands for one face.
    /// Debug mode can collect a bounded sample of what was drawn or culled.
    /// </summary>
    private LidarDrawStats DrawSceneRenderersForFace(CommandBuffer cmd, Camera faceCamera, bool collectSamples)
    {
        LidarDrawStats stats = LidarDrawStats.Empty;
        Plane[] planes = GeometryUtility.CalculateFrustumPlanes(faceCamera);
        Renderer[] renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.None);

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            stats.candidates++;
            if (collectSamples) stats.AddCandidateCategory(renderer);
            if (!ShouldDrawRendererForLidar(renderer, faceCamera, planes, ref stats, collectSamples)) continue;

            int subMeshCount = GetRendererSubMeshCount(renderer);
            for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
            {
                cmd.DrawRenderer(renderer, _linearDepthMaterial, subMesh);
                stats.drawnSubmeshes++;
            }

            stats.drawnRenderers++;
            if (collectSamples)
            {
                stats.AddDrawnSample(renderer);
                stats.AddDrawnCategory(renderer);
            }
        }

        return stats;
    }

    /// <summary>
    /// Applies LiDAR-specific renderer filtering: self/ghost suppression, layer mask checks,
    /// supported renderer types, and face frustum culling.
    /// </summary>
    private bool ShouldDrawRendererForLidar(
        Renderer renderer,
        Camera faceCamera,
        Plane[] planes,
        ref LidarDrawStats stats,
        bool collectSamples)
    {
        if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) return false;
        if (_sourceAgent != null && renderer.transform.IsChildOf(_sourceAgent.transform))
        {
            stats.culledBySelf++;
            if (collectSamples) stats.AddSelfSample(renderer);
            return false;
        }

        if (_sourceSelfRoot != null && renderer.transform.IsChildOf(_sourceSelfRoot))
        {
            stats.culledBySelf++;
            if (collectSamples) stats.AddSelfSample(renderer);
            return false;
        }

        if (_excludedHiddenGhostRoot != null && renderer.transform.IsChildOf(_excludedHiddenGhostRoot))
        {
            stats.culledBySelf++;
            if (collectSamples) stats.AddSelfSample(renderer);
            return false;
        }

        if ((_sourceCamera != null && renderer.GetComponentInParent<LidarSensor>() != null)) return false;
        if ((faceCamera.cullingMask & (1 << renderer.gameObject.layer)) == 0)
        {
            stats.culledByLayer++;
            if (collectSamples) stats.AddLayerSample(renderer);
            return false;
        }

        if (renderer is ParticleSystemRenderer)
        {
            stats.culledByType++;
            if (collectSamples) stats.AddTypeSample(renderer);
            return false;
        }

        if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer))
        {
            stats.culledByType++;
            if (collectSamples) stats.AddTypeSample(renderer);
            return false;
        }

        if (!GeometryUtility.TestPlanesAABB(planes, renderer.bounds))
        {
            stats.culledByFrustum++;
            if (collectSamples) stats.AddFrustumSample(renderer);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Determines how many submeshes should be drawn for a renderer when writing linear depth.
    /// </summary>
    private static int GetRendererSubMeshCount(Renderer renderer)
    {
        Mesh mesh = null;

        if (renderer is MeshRenderer)
        {
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            if (filter != null) mesh = filter.sharedMesh;
        }
        else if (renderer is SkinnedMeshRenderer skinned)
        {
            mesh = skinned.sharedMesh;
        }

        if (mesh != null)
            return Mathf.Max(1, mesh.subMeshCount);

        return Mathf.Max(1, renderer.sharedMaterials != null ? renderer.sharedMaterials.Length : 1);
    }

    /// <summary>
    /// Formats scene-renderer debug counters into a single log-friendly block.
    /// </summary>
    private static string FormatSceneStats(LidarDrawStats stats)
    {
        return
            "scene{" +
            $"candidates={stats.candidates}, " +
            $"culledBySelf={stats.culledBySelf}, " +
            $"culledByLayer={stats.culledByLayer}, " +
            $"culledByType={stats.culledByType}, " +
            $"culledByFrustum={stats.culledByFrustum}, " +
            $"drawnRenderers={stats.drawnRenderers}, " +
            $"drawnSubmeshes={stats.drawnSubmeshes}, " +
            $"categoryCandidates={FormatCategoryStats(stats.candidateCategories)}, " +
            $"categoryDrawn={FormatCategoryStats(stats.drawnCategories)}, " +
            $"drawnSamples=[{stats.drawnSamples}], " +
            $"selfSamples=[{stats.selfSamples}], " +
            $"layerSamples=[{stats.layerSamples}], " +
            $"typeSamples=[{stats.typeSamples}], " +
            $"frustumSamples=[{stats.frustumSamples}]" +
            "}";
    }

    /// <summary>
    /// Formats coarse name-based debug categories such as shelf/item/wall/floor.
    /// </summary>
    private static string FormatCategoryStats(LidarDebugCategoryStats stats)
    {
        return
            "{" +
            $"shelf={stats.shelfLike}, " +
            $"item={stats.itemLike}, " +
            $"light={stats.lightLike}, " +
            $"ceiling={stats.ceilingLike}, " +
            $"wall={stats.wallLike}, " +
            $"floor={stats.floorLike}" +
            "}";
    }

    /// <summary>
    /// Formats GPU-instanced/indirect draw counters for LiDAR debug logs.
    /// </summary>
    private static string FormatIndirectStats(LidarIndirectDrawStats stats)
    {
        return
            "indirect{" +
            $"batchers={stats.batchers}, " +
            $"readyBatchers={stats.readyBatchers}, " +
            $"sourceInstances={stats.sourceInstances}, " +
            $"lods={stats.lods}, " +
            $"submeshes={stats.submeshes}, " +
            $"queuedCommands={stats.queuedCommands}, " +
            $"skippedNotReady={stats.skippedNotReady}, " +
            $"skippedNoInstances={stats.skippedNoInstances}, " +
            $"skippedMissingBuffers={stats.skippedMissingBuffers}" +
            "}";
    }

    /// <summary>
    /// Renders a normal color view for a face so debug captures can prove the face camera sees geometry.
    /// </summary>
    private void RenderFaceColorOnly(int face)
    {
        Camera faceCamera = _faceCameras[face];
        RenderTexture previousTarget = faceCamera.targetTexture;

        try
        {
            faceCamera.transform.SetPositionAndRotation(
                transform.position,
                transform.rotation * _faceLocalRotations[face]);
            faceCamera.targetTexture = _colorTargets[face];
            faceCamera.Render();
        }
        finally
        {
            faceCamera.targetTexture = previousTarget;
        }
    }

    /// <summary>
    /// Dispatches the compute shader that samples the six face depth textures into a flat range array.
    /// Output order is channel-major: range[channelIndex * azimuthSamples + azimuthIndex].
    /// </summary>
    private void DispatchRangeSampler(AzimuthSweep azimuthSweep)
    {
        int kernel = depthSampler.FindKernel("CSMain");

        depthSampler.SetInt("_Channels", channels);
        depthSampler.SetInt("_AzimuthSamples", azimuthSamples);
        depthSampler.SetInt("_FaceResolution", faceResolution);
        depthSampler.SetFloat("_MinRange", minRange);
        depthSampler.SetFloat("_MaxRange", maxRange);
        depthSampler.SetFloat("_AzimuthStartDeg", azimuthSweep.startDeg);
        depthSampler.SetFloat("_AzimuthStepDeg", azimuthSweep.stepDeg);
        depthSampler.SetVector("_ZBufferParamsForLidar", BuildZBufferParams(_faceCameras[0]));
        depthSampler.SetInt("_UsesReversedZBuffer", SystemInfo.usesReversedZBuffer ? 1 : 0);
        depthSampler.SetBuffer(kernel, "_VerticalAnglesDeg", _verticalAnglesBuffer);
        depthSampler.SetBuffer(kernel, "_Ranges", _rangeBuffer);

        for (int face = 0; face < FaceCount; face++)
            depthSampler.SetTexture(kernel, $"_Face{face}Depth", _depthTargets[face]);

        depthSampler.Dispatch(
            kernel,
            Mathf.CeilToInt(azimuthSamples / 8f),
            Mathf.CeilToInt(channels / 8f),
            1);
    }

    /// <summary>
    /// Resolves the horizontal LiDAR sweep described by the selected inspector mode.
    /// </summary>
    private AzimuthSweep ResolveAzimuthSweep()
    {
        switch (sweepMode)
        {
            case SweepMode.EgocentricView:
                return ResolveEgocentricSweep();
            case SweepMode.Full360:
            default:
                return new AzimuthSweep
                {
                    startDeg = 0f,
                    stepDeg = 360f / azimuthSamples
                };
        }
    }

    /// <summary>
    /// Centers the horizontal sweep on the level sensor's local forward direction.
    /// </summary>
    private AzimuthSweep ResolveEgocentricSweep()
    {
        float horizontalFovDeg = CalculateHorizontalFovDeg(_sourceCamera);

        if (azimuthSamples == 1)
        {
            return new AzimuthSweep
            {
                startDeg = 0f,
                stepDeg = 0f
            };
        }

        return new AzimuthSweep
        {
            startDeg = horizontalFovDeg * -0.5f,
            stepDeg = horizontalFovDeg / (azimuthSamples - 1)
        };
    }

    /// <summary>
    /// Converts Unity's vertical camera FOV into the horizontal FOV visible at the current aspect ratio.
    /// </summary>
    private static float CalculateHorizontalFovDeg(Camera camera)
    {
        if (camera == null) return 360f;

        float verticalFovRad = camera.fieldOfView * Mathf.Deg2Rad;
        float aspect = Mathf.Max(camera.aspect, 0.0001f);
        float horizontalFovRad = 2f * Mathf.Atan(Mathf.Tan(verticalFovRad * 0.5f) * aspect);
        return horizontalFovRad * Mathf.Rad2Deg;
    }

    /// <summary>
    /// Converts one RFloat linear-depth face into a grayscale preview texture for debug PNG output.
    /// </summary>
    private void WriteLinearDepthPreview(int face, RenderTexture target)
    {
        int kernel = depthSampler.FindKernel("DepthDebug");
        depthSampler.SetInt("_FaceResolution", faceResolution);
        depthSampler.SetFloat("_MaxRange", maxRange);
        depthSampler.SetTexture(kernel, "_DebugDepth", _depthTargets[face]);
        depthSampler.SetTexture(kernel, "_DebugTexture", target);
        depthSampler.Dispatch(kernel, Mathf.CeilToInt(faceResolution / 8f), Mathf.CeilToInt(faceResolution / 8f), 1);
    }

    /// <summary>
    /// Returns configured vertical channel angles, or evenly distributes them across the vertical FOV.
    /// </summary>
    private float[] GetVerticalAngles()
    {
        if (verticalAnglesDeg != null && verticalAnglesDeg.Length == channels)
            return (float[])verticalAnglesDeg.Clone();

        float[] angles = new float[channels];
        if (channels == 1)
        {
            angles[0] = (verticalFovMinDeg + verticalFovMaxDeg) * 0.5f;
            return angles;
        }

        for (int i = 0; i < channels; i++)
        {
            float t = i / (float)(channels - 1);
            angles[i] = Mathf.Lerp(verticalFovMinDeg, verticalFovMaxDeg, t);
        }

        return angles;
    }

    /// <summary>
    /// Uploads vertical channel angles to a GPU buffer consumed by the range-sampling compute shader.
    /// </summary>
    private void UploadVerticalAngles(float[] angles)
    {
        if (_verticalAnglesBuffer == null || _verticalAnglesBuffer.count != angles.Length)
        {
            _verticalAnglesBuffer?.Release();
            _verticalAnglesBuffer = new ComputeBuffer(angles.Length, sizeof(float));
        }

        _verticalAnglesBuffer.SetData(angles);
    }

    /// <summary>
    /// Ensures the GPU output buffer is sized for one float range per channel/azimuth sample pair.
    /// </summary>
    private void EnsureRangeBuffer()
    {
        int count = channels * azimuthSamples;
        if (_rangeBuffer != null && _rangeBufferCount == count) return;

        _rangeBuffer?.Release();
        _rangeBuffer = new ComputeBuffer(count, sizeof(float));
        _rangeBufferCount = count;
    }

    /// <summary>
    /// Builds the exact binary payload sent by WebSocketSharp's Send(byte[]) API.
    ///
    /// All values are little-endian because BinaryWriter writes little-endian primitives on Unity/.NET:
    ///   bytes  0-3   char[4]  magic "LDR1"
    ///   bytes  4-5   ushort   channels
    ///   bytes  6-7   ushort   azimuthSamples
    ///   bytes  8-11  float    minRange, meters
    ///   bytes 12-15  float    maxRange, meters
    ///   bytes 16-19  float    azimuthStartDeg for the selected sweep mode
    ///   bytes 20-23  float    azimuthStepDeg for the selected sweep mode
    ///   bytes 24-27  uint     sequence, increments per sensor
    ///   bytes 28-35  double   UTC Unix timestamp in seconds
    ///   next channels floats: verticalAnglesDeg[channel]
    ///   next channels * azimuthSamples floats: ranges in meters
    ///
    /// The range array is channel-major:
    ///   ranges[channelIndex * azimuthSamples + azimuthIndex]
    /// No Unity world position, rotation, or object identity is serialized in this payload.
    /// </summary>
    private byte[] BuildPayload(float[] angles, NativeArray<float> ranges, AzimuthSweep azimuthSweep)
    {
        using MemoryStream stream = new MemoryStream();
        using BinaryWriter writer = new BinaryWriter(stream);

        writer.Write(new[] { (byte)Magic[0], (byte)Magic[1], (byte)Magic[2], (byte)Magic[3] });
        writer.Write((ushort)channels);
        writer.Write((ushort)azimuthSamples);
        writer.Write(minRange);
        writer.Write(maxRange);
        writer.Write(azimuthSweep.startDeg);
        writer.Write(azimuthSweep.stepDeg);
        writer.Write((uint)++_sequence);
        writer.Write(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);

        for (int i = 0; i < angles.Length; i++)
            writer.Write(angles[i]);

        for (int i = 0; i < ranges.Length; i++)
            writer.Write(ranges[i]);

        return stream.ToArray();
    }

    /// <summary>
    /// Mirrors Unity's z-buffer parameter math for debug compute kernels that need depth conversion.
    /// </summary>
    private static Vector4 BuildZBufferParams(Camera camera)
    {
        float near = camera.nearClipPlane;
        float far = camera.farClipPlane;
        float farOverNear = far / near;

        if (SystemInfo.usesReversedZBuffer)
            return new Vector4(farOverNear - 1f, 1f, (farOverNear - 1f) / far, 1f / far);

        return new Vector4(1f - farOverNear, farOverNear, (1f - farOverNear) / far, farOverNear / far);
    }

    /// <summary>
    /// Converts a cube face index into a stable debug filename label.
    /// </summary>
    private static string GetFaceName(int face)
    {
        switch (face)
        {
            case 0: return "right";
            case 1: return "left";
            case 2: return "up";
            case 3: return "down";
            case 4: return "forward";
            case 5: return "back";
            default: return "unknown";
        }
    }

    /// <summary>
    /// Saves a render texture as a PNG and returns simple brightness statistics for debug logging.
    /// </summary>
    private TextureStats SaveRenderTexturePng(RenderTexture source, string path)
    {
        RenderTexture previous = RenderTexture.active;
        Texture2D readable = null;

        try
        {
            RenderTexture.active = source;
            readable = new Texture2D(source.width, source.height, TextureFormat.RGB24, false);
            readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            readable.Apply();

            TextureStats stats = CalculateTextureStats(readable);
            File.WriteAllBytes(path, readable.EncodeToPNG());
            return stats;
        }
        finally
        {
            RenderTexture.active = previous;
            if (readable != null) Destroy(readable);
        }
    }

    /// <summary>
    /// Computes non-black/min/max pixel statistics for a debug texture.
    /// </summary>
    private static TextureStats CalculateTextureStats(Texture2D texture)
    {
        Color32[] pixels = texture.GetPixels32();
        TextureStats stats = new TextureStats
        {
            totalPixels = pixels.Length,
            minValue = 255,
            maxValue = 0
        };

        for (int i = 0; i < pixels.Length; i++)
        {
            int value = Mathf.Max(pixels[i].r, Mathf.Max(pixels[i].g, pixels[i].b));
            if (value > 0) stats.nonBlackPixels++;
            if (value < stats.minValue) stats.minValue = value;
            if (value > stats.maxValue) stats.maxValue = value;
        }

        return stats;
    }

    /// <summary>
    /// Computes hit/miss summary stats for the sampled range buffer.
    /// Values at maxRange are treated as misses/no return.
    /// </summary>
    private RangeStats CalculateRangeStats(NativeArray<float> ranges)
    {
        RangeStats stats = new RangeStats
        {
            totalCount = ranges.Length,
            minRange = float.PositiveInfinity,
            maxHitRange = 0f
        };

        for (int i = 0; i < ranges.Length; i++)
        {
            float range = ranges[i];
            if (range >= maxRange - 0.001f)
            {
                stats.maxRangeCount++;
                continue;
            }

            stats.hitCount++;
            if (range < stats.minRange) stats.minRange = range;
            if (range > stats.maxHitRange) stats.maxHitRange = range;
        }

        if (stats.hitCount == 0)
            stats.minRange = 0f;

        return stats;
    }

    /// <summary>
    /// Extracts the LiDAR sample nearest to the requested angles using the same channel-major
    /// range indexing and point reconstruction used by the WebSocket verifier.
    /// </summary>
    private LidarProbeSample BuildLidarProbeSample(
        float[] angles,
        NativeArray<float> ranges,
        AzimuthSweep azimuthSweep,
        float targetAzimuthDeg,
        float targetVerticalDeg)
    {
        int channelIndex = FindNearestVerticalChannel(angles, targetVerticalDeg);
        int azimuthIndex = FindNearestAzimuthIndex(azimuthSweep, targetAzimuthDeg);
        int rangeIndex = channelIndex * azimuthSamples + azimuthIndex;
        float range = rangeIndex >= 0 && rangeIndex < ranges.Length ? ranges[rangeIndex] : maxRange;
        float verticalDeg = angles[channelIndex];
        float azimuthDeg = azimuthSweep.startDeg + azimuthIndex * azimuthSweep.stepDeg;
        Vector3 localDirection = CalculateLidarLocalDirection(azimuthDeg, verticalDeg);
        Vector3 localPoint = localDirection * range;
        Vector3 worldDirection = transform.TransformDirection(localDirection).normalized;
        Vector3 worldPoint = transform.position + worldDirection * range;

        LidarProbeSample probe = new LidarProbeSample
        {
            targetAzimuthDeg = targetAzimuthDeg,
            targetVerticalDeg = targetVerticalDeg,
            channelIndex = channelIndex,
            verticalDeg = verticalDeg,
            azimuthIndex = azimuthIndex,
            azimuthDeg = azimuthDeg,
            rangeIndex = rangeIndex,
            range = range,
            isMaxRange = range >= maxRange - RangeMissTolerance,
            localDirection = localDirection,
            localPoint = localPoint,
            worldDirection = worldDirection,
            worldPoint = worldPoint,
            raycast = BuildLidarProbeRaycast(worldDirection)
        };

        CacheDebugProbeRaycastGizmo(probe);
        return probe;
    }

    private static int FindNearestVerticalChannel(float[] angles, float targetVerticalDeg)
    {
        int bestIndex = 0;
        float bestDelta = float.PositiveInfinity;

        for (int i = 0; i < angles.Length; i++)
        {
            float delta = Mathf.Abs(angles[i] - targetVerticalDeg);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private int FindNearestAzimuthIndex(AzimuthSweep azimuthSweep, float targetAzimuthDeg)
    {
        int bestIndex = 0;
        float bestDelta = float.PositiveInfinity;

        for (int i = 0; i < azimuthSamples; i++)
        {
            float azimuthDeg = azimuthSweep.startDeg + i * azimuthSweep.stepDeg;
            float delta = Mathf.Abs(Mathf.DeltaAngle(azimuthDeg, targetAzimuthDeg));
            if (delta < bestDelta)
            {
                bestDelta = delta;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private static Vector3 CalculateLidarLocalDirection(float azimuthDeg, float verticalDeg)
    {
        float azimuthRad = azimuthDeg * Mathf.Deg2Rad;
        float verticalRad = verticalDeg * Mathf.Deg2Rad;
        float cosVertical = Mathf.Cos(verticalRad);

        return new Vector3(
            Mathf.Sin(azimuthRad) * cosVertical,
            Mathf.Sin(verticalRad),
            Mathf.Cos(azimuthRad) * cosVertical);
    }

    private LidarProbeRaycast BuildLidarProbeRaycast(Vector3 worldDirection)
    {
        LidarProbeRaycast result = new LidarProbeRaycast
        {
            colliderName = "none",
            layerName = "none"
        };

        if (worldDirection.sqrMagnitude <= 0.000001f)
            return result;

        int layerMask = _sourceCamera != null ? _sourceCamera.cullingMask : Physics.DefaultRaycastLayers;
        RaycastHit[] hits = Physics.RaycastAll(
            transform.position,
            worldDirection.normalized,
            maxRange,
            layerMask,
            QueryTriggerInteraction.Ignore);

        int bestIndex = -1;
        float bestDistance = float.PositiveInfinity;
        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit hit = hits[i];
            if (hit.collider == null || hit.distance < minRange) continue;
            if (!ShouldUseProbeRaycastHit(hit.collider.transform)) continue;
            if (hit.distance >= bestDistance) continue;

            bestDistance = hit.distance;
            bestIndex = i;
        }

        if (bestIndex < 0)
            return result;

        RaycastHit bestHit = hits[bestIndex];
        string layerName = LayerMask.LayerToName(bestHit.collider.gameObject.layer);
        if (string.IsNullOrEmpty(layerName))
            layerName = bestHit.collider.gameObject.layer.ToString();

        result.hit = true;
        result.colliderName = bestHit.collider.name;
        result.layerName = layerName;
        result.distance = bestHit.distance;
        result.point = bestHit.point;
        return result;
    }

    private bool ShouldUseProbeRaycastHit(Transform hitTransform)
    {
        if (hitTransform == null) return false;

        if (_sourceAgent != null && hitTransform.IsChildOf(_sourceAgent.transform))
            return false;

        if (_sourceSelfRoot != null && hitTransform.IsChildOf(_sourceSelfRoot))
            return false;

        if (_excludedHiddenGhostRoot != null && hitTransform.IsChildOf(_excludedHiddenGhostRoot))
            return false;

        if (hitTransform.GetComponentInParent<LidarSensor>() != null)
            return false;

        return true;
    }

    private string FormatLidarProbeMessage(LidarProbeSample probe)
    {
        return
            "LiDAR debug step 7 probe: " +
            $"targetAzimuthDeg={probe.targetAzimuthDeg:0.000}, " +
            $"targetVerticalDeg={probe.targetVerticalDeg:0.000}, " +
            $"channel={probe.channelIndex}, " +
            $"verticalDeg={probe.verticalDeg:0.000}, " +
            $"azimuthIndex={probe.azimuthIndex}, " +
            $"azimuthDeg={probe.azimuthDeg:0.000}, " +
            $"rangeIndex={probe.rangeIndex}, " +
            $"range={probe.range:0.000000}, " +
            $"isMaxRange={probe.isMaxRange}, " +
            $"localDirection={FormatVector3(probe.localDirection)}, " +
            $"localPoint={FormatVector3(probe.localPoint)}, " +
            $"worldDirection={FormatVector3(probe.worldDirection)}, " +
            $"worldPoint={FormatVector3(probe.worldPoint)}, " +
            $"raycast={FormatLidarProbeRaycast(probe)}";
    }

    private static string FormatLidarProbeRaycast(LidarProbeSample probe)
    {
        if (!probe.raycast.hit)
            return "noHit";

        return
            "hit{" +
            $"collider={probe.raycast.colliderName}, " +
            $"layer={probe.raycast.layerName}, " +
            $"distance={probe.raycast.distance:0.000000}, " +
            $"rangeDelta={probe.raycast.distance - probe.range:0.000000}, " +
            $"point={FormatVector3(probe.raycast.point)}" +
            "}";
    }

    private static string FormatVector3(Vector3 value)
    {
        return $"({value.x:0.000000}, {value.y:0.000000}, {value.z:0.000000})";
    }

    private void CacheDebugProbeRaycastGizmo(LidarProbeSample probe)
    {
        if (!probe.raycast.hit)
        {
            _hasDebugProbeRaycastGizmo = false;
            return;
        }

        _hasDebugProbeRaycastGizmo = true;
        _debugProbeRayOrigin = transform.position;
        _debugProbeRayEnd = probe.raycast.point;
        _debugProbeRaycastHitPoint = probe.raycast.point;
    }

    private void OnDrawGizmos()
    {
        if (!drawDebugProbeRaycastGizmo || !_hasDebugProbeRaycastGizmo)
            return;

        float radius = Mathf.Max(0.001f, debugProbeRaycastGizmoRadius);

        Gizmos.color = debugProbeRayColor;
        Gizmos.DrawLine(_debugProbeRayOrigin, _debugProbeRayEnd);

        Gizmos.color = debugProbeRaycastHitColor;
        Gizmos.DrawSphere(_debugProbeRaycastHitPoint, radius);
        Gizmos.DrawWireSphere(_debugProbeRaycastHitPoint, radius * 2f);
    }

    /// <summary>
    /// Legacy renderer-toggle helper retained for debugging paths that need to hide the source avatar.
    /// Current LiDAR rendering primarily uses renderer filtering instead of mutating visibility.
    /// </summary>
    private static List<RendererState> HideSourceAgentRenderers(Camera sourceCamera)
    {
        Transform root = ResolveSelfRoot(sourceCamera);

        if (root == null) return null;

        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        List<RendererState> states = new List<RendererState>(renderers.Length);

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null) continue;

            states.Add(new RendererState
            {
                renderer = renderer,
                wasEnabled = renderer.enabled
            });
            renderer.enabled = false;
        }

        return states;
    }

    /// <summary>
    /// Finds the hidden ghost corresponding to the source camera so it can be excluded from LiDAR renders.
    /// </summary>
    private static HumanoidGhostFollower ResolveHiddenGhost(Camera sourceCamera, HumanoidGhostFollower explicitGhost)
    {
        if (explicitGhost != null) return explicitGhost;
        if (sourceCamera == null) return null;

        HumanoidGhostCameraVisibility cameraVisibility =
            sourceCamera.GetComponent<HumanoidGhostCameraVisibility>();
        if (cameraVisibility != null && cameraVisibility.Ghost != null)
            return cameraVisibility.Ghost;

        AgentControllerBase sourceAgent = sourceCamera.GetComponentInParent<AgentControllerBase>();
        HumanoidGhostFollower[] ghosts = FindObjectsByType<HumanoidGhostFollower>(FindObjectsSortMode.None);

        if (sourceAgent != null)
        {
            for (int i = 0; i < ghosts.Length; i++)
            {
                HumanoidGhostFollower ghost = ghosts[i];
                if (ghost != null && ghost.Authority == sourceAgent)
                    return ghost;
            }
        }

        Vector3 referencePosition =
            sourceAgent != null && sourceAgent.MovementRoot != null
                ? sourceAgent.MovementRoot.position
                : sourceCamera.transform.position;

        HumanoidGhostFollower closest = null;
        float closestSqrDistance = HiddenGhostFallbackMaxDistance * HiddenGhostFallbackMaxDistance;

        for (int i = 0; i < ghosts.Length; i++)
        {
            HumanoidGhostFollower ghost = ghosts[i];
            if (ghost == null || !ghost.isActiveAndEnabled) continue;

            float sqrDistance = (ghost.transform.position - referencePosition).sqrMagnitude;
            if (sqrDistance > closestSqrDistance) continue;

            closest = ghost;
            closestSqrDistance = sqrDistance;
        }

        return closest;
    }

    /// <summary>
    /// Resolves the transform considered "self" for renderer filtering.
    /// </summary>
    private static Transform ResolveSelfRoot(Camera sourceCamera)
    {
        if (sourceCamera == null) return null;

        AgentControllerBase agent = sourceCamera.GetComponentInParent<AgentControllerBase>();
        if (agent != null) return agent.transform;

        Rigidbody body = sourceCamera.GetComponentInParent<Rigidbody>();
        if (body != null) return body.transform;

        return sourceCamera.transform.root;
    }

    /// <summary>
    /// Restores renderer enabled states captured by HideSourceAgentRenderers.
    /// </summary>
    private static void RestoreRenderers(List<RendererState> states)
    {
        if (states == null) return;

        for (int i = 0; i < states.Count; i++)
        {
            Renderer renderer = states[i].renderer;
            if (renderer != null)
                renderer.enabled = states[i].wasEnabled;
        }
    }

    private struct LidarDrawStats
    {
        public int candidates;
        public int culledBySelf;
        public int culledByLayer;
        public int culledByType;
        public int culledByFrustum;
        public int drawnRenderers;
        public int drawnSubmeshes;
        public int drawnSampleCount;
        public int selfSampleCount;
        public int layerSampleCount;
        public int typeSampleCount;
        public int frustumSampleCount;
        public string drawnSamples;
        public string selfSamples;
        public string layerSamples;
        public string typeSamples;
        public string frustumSamples;
        public LidarDebugCategoryStats candidateCategories;
        public LidarDebugCategoryStats drawnCategories;

        public static LidarDrawStats Empty => new LidarDrawStats
        {
            drawnSamples = "",
            selfSamples = "",
            layerSamples = "",
            typeSamples = "",
            frustumSamples = ""
        };

        // Records a bounded sample of drawn/cull categories for forward-face debug logs.
        public void AddDrawnSample(Renderer renderer) =>
            AddSample(ref drawnSamples, ref drawnSampleCount, renderer);

        public void AddSelfSample(Renderer renderer) =>
            AddSample(ref selfSamples, ref selfSampleCount, renderer);

        public void AddLayerSample(Renderer renderer) =>
            AddSample(ref layerSamples, ref layerSampleCount, renderer);

        public void AddTypeSample(Renderer renderer) =>
            AddSample(ref typeSamples, ref typeSampleCount, renderer);

        public void AddFrustumSample(Renderer renderer) =>
            AddSample(ref frustumSamples, ref frustumSampleCount, renderer);

        public void AddCandidateCategory(Renderer renderer) =>
            candidateCategories.Add(renderer);

        public void AddDrawnCategory(Renderer renderer) =>
            drawnCategories.Add(renderer);

        // Appends at most DebugSampleLimit renderer descriptions, then marks the sample as truncated.
        private static void AddSample(ref string target, ref int count, Renderer renderer)
        {
            if (count >= DebugSampleLimit)
            {
                count++;
                return;
            }

            if (!string.IsNullOrEmpty(target))
                target += "; ";

            string layerName = LayerMask.LayerToName(renderer.gameObject.layer);
            if (string.IsNullOrEmpty(layerName))
                layerName = renderer.gameObject.layer.ToString();

            target += $"{renderer.GetType().Name}:{renderer.name}@{layerName}";
            count++;

            if (count == DebugSampleLimit)
                target += "; ...";
        }
    }

    private struct LidarDebugCategoryStats
    {
        public int shelfLike;
        public int itemLike;
        public int lightLike;
        public int ceilingLike;
        public int wallLike;
        public int floorLike;

        // Coarsely categorizes renderers by name/parent names for fast debug summaries.
        public void Add(Renderer renderer)
        {
            string name = BuildCategoryText(renderer);

            if (name.Contains("shelf")) shelfLike++;
            if (name.Contains("item") || name.Contains("product") || name.Contains("row_") || name.Contains("row item")) itemLike++;
            if (name.Contains("light") || name.Contains("lamp")) lightLike++;
            if (name.Contains("ceiling")) ceilingLike++;
            if (name.Contains("wall")) wallLike++;
            if (name.Contains("floor") || name.Contains("ground")) floorLike++;
        }

        // Builds the lower-case name context used by the coarse debug categorizer.
        private static string BuildCategoryText(Renderer renderer)
        {
            string text = renderer.name;
            Transform current = renderer.transform.parent;
            int depth = 0;

            while (current != null && depth < 4)
            {
                text += " " + current.name;
                current = current.parent;
                depth++;
            }

            return text.ToLowerInvariant();
        }
    }

    private struct TextureStats
    {
        public int totalPixels;
        public int nonBlackPixels;
        public int minValue;
        public int maxValue;
    }

    private struct AzimuthSweep
    {
        public float startDeg;
        public float stepDeg;
    }

    private struct RangeStats
    {
        public int totalCount;
        public int hitCount;
        public int maxRangeCount;
        public float minRange;
        public float maxHitRange;
    }

    private struct LidarProbeSample
    {
        public float targetAzimuthDeg;
        public float targetVerticalDeg;
        public int channelIndex;
        public float verticalDeg;
        public int azimuthIndex;
        public float azimuthDeg;
        public int rangeIndex;
        public float range;
        public bool isMaxRange;
        public Vector3 localDirection;
        public Vector3 localPoint;
        public Vector3 worldDirection;
        public Vector3 worldPoint;
        public LidarProbeRaycast raycast;
    }

    private struct LidarProbeRaycast
    {
        public bool hit;
        public string colliderName;
        public string layerName;
        public float distance;
        public Vector3 point;
    }

    private struct RendererState
    {
        public Renderer renderer;
        public bool wasEnabled;
    }

    /// <summary>
    /// Releases GPU buffers, render textures, materials, and hidden face cameras owned by this sensor.
    /// </summary>
    private void OnDestroy()
    {
        _verticalAnglesBuffer?.Release();
        _rangeBuffer?.Release();
        if (_linearDepthMaterial != null) Destroy(_linearDepthMaterial);

        for (int i = 0; i < FaceCount; i++)
        {
            ReleaseRenderTexture(_colorTargets[i]);
            ReleaseRenderTexture(_depthTargets[i]);

            if (_faceCameras[i] != null)
                Destroy(_faceCameras[i].gameObject);
        }
    }

    /// <summary>
    /// Releases and destroys one render texture if it exists.
    /// </summary>
    private static void ReleaseRenderTexture(RenderTexture texture)
    {
        if (texture == null) return;
        texture.Release();
        Destroy(texture);
    }
}
