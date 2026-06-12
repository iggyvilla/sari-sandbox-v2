using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Produces a sampleable R32_SFloat copy of the camera depth for the Hi-Z occlusion pyramid.
//
// Why a ScriptableRendererFeature instead of the runtime-enqueued pass we tried first:
//  - It binds the camera depth ATTACHMENT (renderer.cameraDepthTargetHandle) and copies it using
//    URP's CopyDepthPass pattern: sample non-MSAA depth as a depth texture, and only LOAD MSAA
//    depth. Loading a non-MSAA D32_SFloat_S8_UInt depth-stencil texture on Metal returns 0.
//  - AddRenderPasses runs at renderer-setup time, so ConfigureInput(Depth) actually causes URP
//    to produce/keep depth. A pass enqueued at beginCameraRendering requests it too late.
//
// SETUP (one manual step): add this feature to the Renderer Features list on
// Assets/Settings/PC_PipelineAsset_ForwardRenderer.asset. Everything else self-wires.
// HiZOcclusionManager consumes HiZDepthCopyFeature.DepthCopy as the pyramid source.
public class HiZDepthCopyFeature : ScriptableRendererFeature
{
    /// <summary>Sampleable R32F copy of camera depth (reversed-Z). Null until the first copy.</summary>
    public static RenderTexture DepthCopy { get; private set; }
    /// <summary>Time.frameCount of the most recent copy — consumers check this for freshness.</summary>
    public static int LastCopyFrame { get; private set; } = -1;
    /// <summary>Diagnostic: remap output to 0.5 + 0.5*depth (driven by HiZOcclusionManager.debugPattern).</summary>
    public static bool ForceMark;

    [SerializeField] private Shader copyShader;
    [Tooltip("After transparents = after depth is fully written. Leave unless you know why.")]
    [SerializeField] private RenderPassEvent injectionPoint = RenderPassEvent.AfterRenderingTransparents;

    private Material _material;
    private CopyPass _pass;

    public override void Create()
    {
        if (copyShader == null) copyShader = Shader.Find("Hidden/HiZCopyDepth");
        if (copyShader != null) _material = CoreUtils.CreateEngineMaterial(copyShader);
        else Debug.LogError("HiZDepthCopyFeature: 'Hidden/HiZCopyDepth' shader not found.");
        _pass = new CopyPass(_material) { renderPassEvent = injectionPoint };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_material == null) return;
        if (renderingData.cameraData.cameraType != CameraType.Game) return;
        // Only the agent/main camera that drives culling
        if (GPUInstanceTracker.Instance != null &&
            renderingData.cameraData.camera != GPUInstanceTracker.Instance.MainCamera) return;

        EnsureTarget(renderingData.cameraData.cameraTargetDescriptor);
        _pass.Setup(DepthCopy);
        renderer.EnqueuePass(_pass);
    }

    private static void EnsureTarget(RenderTextureDescriptor camDesc)
    {
        int w = Mathf.Max(camDesc.width, 1), h = Mathf.Max(camDesc.height, 1);
        if (DepthCopy != null && DepthCopy.width == w && DepthCopy.height == h) return;
        if (DepthCopy != null) DepthCopy.Release();
        DepthCopy = new RenderTexture(w, h, 0, RenderTextureFormat.RFloat)
        {
            name = "HiZDepthCopy",
            filterMode = FilterMode.Point
        };
        DepthCopy.Create();
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(_material);
        if (DepthCopy != null) { DepthCopy.Release(); DepthCopy = null; }
    }

    private class CopyPass : ScriptableRenderPass
    {
        private readonly Material _material;
        private RenderTexture _target;
        private static bool _logged;
        private static readonly int ForceMarkId = Shader.PropertyToID("_HiZForceMark");
        private static readonly int SourceId = Shader.PropertyToID("_HiZDepthSource");
        private static readonly int SourceTexelSizeId = Shader.PropertyToID("_HiZDepthSource_TexelSize");
        private static readonly string[] MsaaKeywords = { "_HIZ_MSAA2", "_HIZ_MSAA4", "_HIZ_MSAA8" };

        // The depth attachment is multisampled when the asset has MSAA on; a plain Texture2D
        // bind of it is illegal on Metal. Pick the Texture2DMS variant matching its sample count.
        private void SetMsaaKeyword(int samples)
        {
            foreach (string kw in MsaaKeywords) _material.DisableKeyword(kw);
            if (samples == 2) _material.EnableKeyword("_HIZ_MSAA2");
            else if (samples == 4) _material.EnableKeyword("_HIZ_MSAA4");
            else if (samples == 8) _material.EnableKeyword("_HIZ_MSAA8");
        }

        public CopyPass(Material material)
        {
            _material = material;
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        public void Setup(RenderTexture target) => _target = target;

#pragma warning disable CS0672, CS0618 // compatibility-mode Execute (Render Graph disabled in this project)
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (_material == null || _target == null) return;
            RTHandle depth = renderingData.cameraData.renderer.cameraDepthTargetHandle;
            if (depth == null || depth.rt == null) return;

            _material.SetFloat(ForceMarkId, ForceMark ? 1f : 0f);
            _material.SetVector(SourceTexelSizeId, new Vector4(
                1f / depth.rt.width,
                1f / depth.rt.height,
                depth.rt.width,
                depth.rt.height));
            SetMsaaKeyword(depth.rt.antiAliasing);

            CommandBuffer cmd = CommandBufferPool.Get("HiZ Depth Copy");
            // Bind the depth attachment explicitly. SetRenderTarget(_target) frees the depth
            // attachment for reading by the shader.
            cmd.SetGlobalTexture(SourceId, depth);
            CoreUtils.SetRenderTarget(cmd, _target);
            CoreUtils.DrawFullScreen(cmd, _material);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            LastCopyFrame = Time.frameCount;
            if (!_logged)
            {
                _logged = true;
                Debug.Log($"HiZDepthCopyFeature: first depth copy executed — attachment {depth.rt.width}x{depth.rt.height} " +
                          $"format={depth.rt.graphicsFormat}, depthStencil={depth.rt.depthStencilFormat}, " +
                          $"MSAA={depth.rt.antiAliasing} (non-MSAA samples, MSAA loads).");
            }
        }
#pragma warning restore CS0672, CS0618
    }
}
