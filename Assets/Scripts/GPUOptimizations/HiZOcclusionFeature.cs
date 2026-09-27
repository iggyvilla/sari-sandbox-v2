using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Builds a farthest-depth Hi-Z pyramid from this frame's depth prepass, then occlusion-culls the
// GPU-instanced products before the opaque pass draws them (no frame lag, no extra scene render).
public sealed class HiZOcclusionFeature : ScriptableRendererFeature
{
    [Tooltip("Meters an instance must sit behind the occluder depth before it is culled.")]
    [Min(0f)] public float depthBias = 0.05f;

    [Tooltip("Log Hi-Z mip 0 depth range every 120 frames (debugging).")]
    public bool logDepthRange;

    private HiZOcclusionPass _pass;

    public override void Create()
    {
        Shader copyShader = Resources.Load<Shader>("HiZCopyDepth");
        ComputeShader downsample = Resources.Load<ComputeShader>("HiZDownsample");
        _pass = copyShader != null && downsample != null ? new HiZOcclusionPass(copyShader, downsample) : null;
        if (_pass == null)
            Debug.LogWarning("HiZOcclusionFeature: HiZCopyDepth/HiZDownsample resources missing; occlusion disabled.");
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        GPUInstanceTracker tracker = GPUInstanceTracker.Instance;
        Camera cam = renderingData.cameraData.camera;
        if (_pass == null || tracker == null || !tracker.OcclusionCullingEnabled ||
            cam.cameraType != CameraType.Game || !GPUInstanceTracker.DrawsProducts(cam))
            return;

        _pass.Setup(tracker, depthBias, logDepthRange);
        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing) => _pass?.Dispose();

    private sealed class HiZOcclusionPass : ScriptableRenderPass
    {
        private static readonly int SrcId = Shader.PropertyToID("_Src");
        private static readonly int DstId = Shader.PropertyToID("_Dst");
        private static readonly int SizesId = Shader.PropertyToID("_Sizes");

        private readonly Material _copyMaterial;
        private readonly ComputeShader _downsample;
        private readonly int _reduceKernel;
        private readonly Dictionary<Camera, RenderTexture> _pyramids = new();
        private readonly OcclusionView _view = new();
        private GPUInstanceTracker _tracker;
        private float _depthBias;
        private bool _logDepthRange;

        public HiZOcclusionPass(Shader copyShader, ComputeShader downsample)
        {
            _copyMaterial = CoreUtils.CreateEngineMaterial(copyShader);
            _downsample = downsample;
            _reduceKernel = downsample.FindKernel("Reduce");
            profilingSampler = new ProfilingSampler("HiZ Occlusion Culling");
            // A depth-reading pass before the opaques forces URP's depth prepass.
            renderPassEvent = RenderPassEvent.AfterRenderingPrePasses;
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        public void Setup(GPUInstanceTracker tracker, float depthBias, bool logDepthRange)
        {
            _tracker = tracker;
            _depthBias = depthBias;
            _logDepthRange = logDepthRange;
        }

#pragma warning disable CS0618, CS0672 // The project runs URP in render-graph compatibility mode.
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            Camera cam = renderingData.cameraData.camera;
            RenderTextureDescriptor target = renderingData.cameraData.cameraTargetDescriptor;
            RenderTexture hiz = EnsurePyramid(cam, target.width, target.height);

            CommandBuffer cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, profilingSampler))
            {
                cmd.SetRenderTarget(hiz, 0);
                cmd.DrawProcedural(Matrix4x4.identity, _copyMaterial, 0, MeshTopology.Triangles, 3);
                if (_logDepthRange && Time.frameCount % 120 == 0)
                    cmd.RequestAsyncReadback(hiz, 0, request => LogDepthRange(cam.name, request));

                int width = hiz.width;
                int height = hiz.height;
                for (int mip = 1; mip < hiz.mipmapCount; mip++)
                {
                    int dstWidth = Mathf.Max(1, width >> 1);
                    int dstHeight = Mathf.Max(1, height >> 1);
                    cmd.SetComputeTextureParam(_downsample, _reduceKernel, SrcId, hiz, mip - 1);
                    cmd.SetComputeTextureParam(_downsample, _reduceKernel, DstId, hiz, mip);
                    cmd.SetComputeVectorParam(_downsample, SizesId, new Vector4(width, height, dstWidth, dstHeight));
                    cmd.DispatchCompute(
                        _downsample, _reduceKernel, (dstWidth + 7) / 8, (dstHeight + 7) / 8, 1);
                    width = dstWidth;
                    height = dstHeight;
                }

                Matrix4x4 projection = cam.projectionMatrix;
                _view.hiz = hiz;
                _view.worldToView = cam.worldToCameraMatrix;
                _view.projection = new Vector4(projection.m00, projection.m11, cam.nearClipPlane, _depthBias);
                _view.size = new Vector4(hiz.width, hiz.height, hiz.mipmapCount, 0f);
                _tracker.RecordOcclusionCull(cmd, cam, _view);

                // Hand the camera targets back so the opaque pass doesn't draw into the pyramid.
                ScriptableRenderer renderer = renderingData.cameraData.renderer;
                cmd.SetRenderTarget(renderer.cameraColorTargetHandle, renderer.cameraDepthTargetHandle);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
#pragma warning restore CS0618, CS0672

        private static void LogDepthRange(string cameraName, AsyncGPUReadbackRequest request)
        {
            if (request.hasError)
            {
                Debug.LogWarning($"HiZ {cameraName}: readback failed");
                return;
            }

            var depths = request.GetData<float>();
            float min = float.MaxValue, max = float.MinValue;
            foreach (float depth in depths)
            {
                min = Mathf.Min(min, depth);
                max = Mathf.Max(max, depth);
            }
            Debug.Log($"HiZ {cameraName} {request.width}x{request.height}: depth {min:F2}..{max:F2}m");
        }

        private RenderTexture EnsurePyramid(Camera cam, int width, int height)
        {
            if (_pyramids.TryGetValue(cam, out RenderTexture hiz) &&
                hiz != null && hiz.width == width && hiz.height == height)
                return hiz;

            if (hiz != null) CoreUtils.Destroy(hiz);
            hiz = new RenderTexture(width, height, 0, RenderTextureFormat.RFloat)
            {
                name = $"HiZ {cam.name}",
                useMipMap = true,
                autoGenerateMips = false,
                enableRandomWrite = true,
                filterMode = FilterMode.Point
            };
            hiz.Create();
            _pyramids[cam] = hiz;
            return hiz;
        }

        public void Dispose()
        {
            foreach (RenderTexture hiz in _pyramids.Values)
                CoreUtils.Destroy(hiz);
            _pyramids.Clear();
            CoreUtils.Destroy(_copyMaterial);
        }
    }
}
