using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Builds a min-filtered ("farthest occluder wins") Hi-Z mip pyramid from the camera depth and
// exposes it (plus the matching view-projection) to FrustumCullingFilterer.compute, which tests
// instance bounding spheres against it.
//
// Depth capture is done by HiZDepthCopyFeature (a ScriptableRendererFeature you add to
// PC_PipelineAsset_ForwardRenderer.asset) — it copies the depth attachment into a sampleable
// R32F RT. This manager reads that copy at endCameraRendering and downsamples it. See
// HiZDepthCopyFeature for the long story on why a feature + LOAD is the only thing that works
// on Metal here.
//
// ASSUMPTIONS (each verifiable through OcclusionDebugger — V/B/N keys or context menu):
//  A1 HiZDepthCopyFeature is in the renderer's feature list and produces DepthCopy.
//     Check: feature's first-copy log + manager's first-capture log + hiz_source_raw.png non-black.
//  A2 Reversed-Z depth (1 = near, 0 = far). Checked in Awake; raw dump must show near geometry
//     white, far geometry black.
//  A3 The depth RT's row order matches clip space of GL.GetGPUProjectionMatrix(proj, true).
//     If records show UVs vertically mirrored vs. screen position, toggle flipY at runtime.
//  A6 Culling consumes this pyramid with 1 frame of latency (BatchInstancer dispatches in
//     Update, before this frame's capture). Matrix, camera position and depth are snapshotted
//     together in the same callback so they can never mix frames.
public class HiZOcclusionManager : MonoBehaviour
{
    public static HiZOcclusionManager Instance { get; private set; }

    [SerializeField] private ComputeShader downsampleShader; // auto-loaded from Resources if unset

    [Tooltip("Master switch. Leave OFF until the depth-map dumps look right (A1–A3).")]
    public bool occlusionEnabled = false;

    [Tooltip("Flip V when sampling the Hi-Z map (assumption A3). Toggle at runtime if occlusion culls wrong items.")]
    public bool flipY = false;

    [Tooltip("Reversed-Z bias added to the sphere depth before comparing against occluders (fights z-fighting flicker).")]
    public float depthBias = 1e-4f;

    [Tooltip("Log per-itemId bounding-sphere radius ranges when instancer buffers rebuild (assumption A5).")]
    public bool logRadii = false;

    [Tooltip("DIAGNOSTIC: remap the depth copy to 0.5+0.5*depth. In hiz_source_raw.png: mid-gray = " +
             "copy ran but depth read 0; black = copy never ran; varying = depth read works.")]
    public bool debugPattern = false;

    public RenderTexture HiZTexture { get; private set; }
    public int HiZMipCount { get; private set; }
    public Vector4 HiZTextureSize { get; private set; }     // mip0: w, h, 1/w, 1/h
    public Matrix4x4 HiZViewProj { get; private set; }      // GPU proj (render-into-texture) * worldToCamera
    public Vector2 HiZProjScale { get; private set; }       // |P[0][0]|, |P[1][1]| of the GPU projection
    public Vector3 CameraPosition { get; private set; }
    public float CapturedNear { get; private set; }          // for the debugger's linearized dump
    public float CapturedFar { get; private set; }

    public bool OcclusionEnabled => occlusionEnabled;
    public bool FlipY => flipY;
    public float DepthBias => depthBias;
    public static bool LogRadii => Instance != null && Instance.logRadii;

    /// <summary>The raw depth copy feeding mip 0 — dumped by OcclusionDebugger as hiz_source_*.png.</summary>
    public RenderTexture DepthSourceRT => HiZDepthCopyFeature.DepthCopy;

    // Valid = captured this frame or the previous one (the accepted 1-frame latency, A6).
    // Goes false on first frames, avatar swaps, or if depth capture breaks — consumers then
    // fall back to frustum-only culling instead of culling against stale data.
    public bool IsValid => HiZTexture != null && Time.frameCount - _lastCaptureFrame <= 1;

    private int _lastCaptureFrame = int.MinValue;
    private bool _loggedFirstCapture;
    private bool _loggedNoFeature;
    private int _copyKernel = -1;
    private int _downKernel = -1;
    private Camera _overriddenCamera;
    private int _invalidFrames;

    private static readonly int SourceDepthId = Shader.PropertyToID("_SourceDepth");
    private static readonly int SrcMipId = Shader.PropertyToID("_SrcMip");
    private static readonly int DstMipId = Shader.PropertyToID("_DstMip");
    private static readonly int SrcSizeId = Shader.PropertyToID("_SrcSize");
    private static readonly int DstSizeId = Shader.PropertyToID("_DstSize");
    private static readonly int DebugParamsId = Shader.PropertyToID("_DebugParams");

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;

        // A2: the whole min-pyramid + comparison direction assumes reversed-Z.
        if (!SystemInfo.usesReversedZBuffer)
            Debug.LogError("HiZOcclusionManager: platform does NOT use reversed-Z — assumption A2 is broken " +
                           "(min pyramid and depth comparison signs would both be inverted here).");

        if (downsampleShader == null)
            downsampleShader = Resources.Load<ComputeShader>("HiZDownsample");
        if (downsampleShader == null)
        {
            Debug.LogError("HiZOcclusionManager: HiZDownsample.compute not found in a Resources folder — occlusion disabled.");
            enabled = false;
            return;
        }

        _copyKernel = downsampleShader.FindKernel("CopyDepth");
        _downKernel = downsampleShader.FindKernel("DownsampleMin");
    }

    void OnEnable()  => RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
    void OnDisable() => RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;

    void Update()
    {
        // Re-resolve every frame: the agent camera is spawned/replaced at runtime (avatar swaps).
        Camera cam = GPUInstanceTracker.Instance != null ? GPUInstanceTracker.Instance.MainCamera : null;
        if (cam != null && cam != _overriddenCamera)
        {
            UniversalAdditionalCameraData data = cam.GetUniversalAdditionalCameraData();
            if (data != null) data.requiresDepthOption = CameraOverrideOption.On;
            _overriddenCamera = cam;
        }

        // Route the diagnostic toggle to the depth-copy shader
        HiZDepthCopyFeature.ForceMark = debugPattern;

        if (occlusionEnabled && !IsValid)
        {
            if (++_invalidFrames == 60)
                Debug.LogError("HiZOcclusionManager: occlusion enabled but no depth captured for 60 frames. " +
                               "Is HiZDepthCopyFeature added to PC_PipelineAsset_ForwardRenderer? Culling is frustum-only.");
        }
        else
        {
            _invalidFrames = 0;
        }
    }

    void OnDestroy()
    {
        if (HiZTexture != null) HiZTexture.Release();
        if (Instance == this) Instance = null;
    }

    private void OnEndCameraRendering(ScriptableRenderContext ctx, Camera cam)
    {
        if (_copyKernel < 0) return;
        if (GPUInstanceTracker.Instance == null || cam != GPUInstanceTracker.Instance.MainCamera) return;

        // Source = the feature's depth copy, written this frame at AfterRenderingTransparents.
        RenderTexture src = HiZDepthCopyFeature.DepthCopy;
        if (src == null || HiZDepthCopyFeature.LastCopyFrame != Time.frameCount)
        {
            if (!_loggedNoFeature)
            {
                _loggedNoFeature = true;
                Debug.LogWarning("HiZOcclusionManager: no fresh depth copy this frame. Add HiZDepthCopyFeature to " +
                                 "Assets/Settings/PC_PipelineAsset_ForwardRenderer.asset's Renderer Features list.");
            }
            return;
        }

        EnsureTexture(src.width, src.height);

        // Mip 0: min-gather the depth copy into the POT base level.
        // Sizes go through SetVector, NOT SetInts: on Metal SetInts uses 16-byte array packing,
        // so an int2's .y reads as 0 in the shader and every thread early-outs.
        downsampleShader.SetTexture(_copyKernel, SourceDepthId, src);
        downsampleShader.SetTexture(_copyKernel, DstMipId, HiZTexture, 0);
        downsampleShader.SetVector(SrcSizeId, new Vector4(src.width, src.height, 0, 0));
        downsampleShader.SetVector(DstSizeId, new Vector4(HiZTexture.width, HiZTexture.height, 0, 0));
        downsampleShader.SetVector(DebugParamsId, Vector4.zero);
        Dispatch(_copyKernel, HiZTexture.width, HiZTexture.height);

        // Mips 1..N: 2x2 min reduction
        int w = HiZTexture.width, h = HiZTexture.height;
        for (int mip = 1; mip < HiZMipCount; mip++)
        {
            w = Mathf.Max(1, w >> 1);
            h = Mathf.Max(1, h >> 1);
            downsampleShader.SetTexture(_downKernel, SrcMipId, HiZTexture, mip - 1);
            downsampleShader.SetTexture(_downKernel, DstMipId, HiZTexture, mip);
            downsampleShader.SetVector(DstSizeId, new Vector4(w, h, 0, 0));
            Dispatch(_downKernel, w, h);
        }

        // A6: snapshot matrix/camera state in the same callback as the depth so the consumer's
        // 1-frame-old data is at least internally consistent.
        Matrix4x4 gpuProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
        HiZViewProj    = gpuProj * cam.worldToCameraMatrix;
        HiZProjScale   = new Vector2(Mathf.Abs(gpuProj.m00), Mathf.Abs(gpuProj.m11));
        CameraPosition = cam.transform.position;
        CapturedNear   = cam.nearClipPlane;
        CapturedFar    = cam.farClipPlane;
        _lastCaptureFrame = Time.frameCount;

        if (!_loggedFirstCapture)
        {
            _loggedFirstCapture = true;
            Debug.Log($"HiZOcclusionManager: first depth capture OK (A1) — copy {src.width}x{src.height}, " +
                      $"HiZ {HiZTexture.width}x{HiZTexture.height}, {HiZMipCount} mips, reversedZ={SystemInfo.usesReversedZBuffer}.");
        }
    }

    private void Dispatch(int kernel, int w, int h) =>
        downsampleShader.Dispatch(kernel, Mathf.CeilToInt(w / 8f), Mathf.CeilToInt(h / 8f), 1);

    private void EnsureTexture(int srcW, int srcH)
    {
        // Power-of-two ≤ half the source: keeps the mip footprint math in the culling shader
        // exact (every mip transition is a clean 2x2).
        int w = FloorPowerOfTwo(Mathf.Max(srcW / 2, 1));
        int h = FloorPowerOfTwo(Mathf.Max(srcH / 2, 1));
        if (HiZTexture != null && HiZTexture.width == w && HiZTexture.height == h) return;

        if (HiZTexture != null) HiZTexture.Release();
        HiZTexture = new RenderTexture(w, h, 0, RenderTextureFormat.RFloat)
        {
            name = "HiZOcclusionPyramid",
            useMipMap = true,
            autoGenerateMips = false,
            enableRandomWrite = true,
            filterMode = FilterMode.Point
        };
        HiZTexture.Create();
        int mips = 1;
        for (int m = Mathf.Max(w, h); m > 1; m >>= 1) mips++;
        HiZMipCount = mips;
        HiZTextureSize = new Vector4(w, h, 1f / w, 1f / h);
        _lastCaptureFrame = int.MinValue;
    }

    private static int FloorPowerOfTwo(int v)
    {
        int pot = Mathf.ClosestPowerOfTwo(v);
        return Mathf.Max(1, pot > v ? pot >> 1 : pot);
    }
}
