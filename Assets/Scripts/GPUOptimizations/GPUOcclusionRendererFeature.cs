using System;
using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public sealed class GPUOcclusionRendererFeature : ScriptableRendererFeature
{
    [Serializable]
    public sealed class Settings
    {
        public ComputeShader occlusionShader;
        public Shader occluderDepthShader;
    }

    [SerializeField] private Settings settings = new();

    private GPUOcclusionRenderPass _pass;
    private static GPUOcclusionRendererFeature _activeFeature;

    public static bool IsOcclusionSupported(Camera camera)
    {
        if (_activeFeature == null ||
            !_activeFeature.isActive ||
            _activeFeature.settings.occlusionShader == null ||
            _activeFeature.settings.occluderDepthShader == null ||
            !SystemInfo.supportsComputeShaders ||
            camera == null ||
            camera.cameraType != CameraType.Game ||
            camera.stereoEnabled)
        {
            return false;
        }

        if (!SystemInfo.IsFormatSupported(
                GraphicsFormat.R32_SFloat,
                GraphicsFormatUsage.Render) ||
            !SystemInfo.IsFormatSupported(
                GraphicsFormat.R32_SFloat,
                GraphicsFormatUsage.Sample))
        {
            return false;
        }

        GPUInstanceTracker tracker = GPUInstanceTracker.Instance;
        if (tracker != null &&
            tracker.OcclusionMode != OcclusionCullingMode.Aggressive &&
            !SystemInfo.IsFormatSupported(
                GraphicsFormat.R32_SFloat,
                GraphicsFormatUsage.LoadStore))
        {
            return false;
        }

        return !camera.TryGetComponent(out UniversalAdditionalCameraData cameraData) ||
               cameraData.renderType == CameraRenderType.Base;
    }

    public override void Create()
    {
        _activeFeature = this;
        _pass?.Dispose();
        _pass = settings.occlusionShader != null && settings.occluderDepthShader != null
            ? new GPUOcclusionRenderPass(
                settings.occlusionShader,
                settings.occluderDepthShader)
            : null;
    }

    public override void AddRenderPasses(
        ScriptableRenderer renderer,
        ref RenderingData renderingData)
    {
        GPUInstanceTracker tracker = GPUInstanceTracker.Instance;
        Camera camera = renderingData.cameraData.camera;
        if (_pass == null ||
            tracker == null ||
            tracker.OcclusionMode == OcclusionCullingMode.Disabled ||
            camera != tracker.MainCamera ||
            !IsCompatibleCamera(ref renderingData))
        {
            return;
        }

        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        if (_activeFeature == this)
            _activeFeature = null;
        _pass?.Dispose();
        _pass = null;
    }

    private static bool IsCompatibleCamera(ref RenderingData renderingData)
    {
        Camera camera = renderingData.cameraData.camera;
        return IsOcclusionSupported(camera) &&
               renderingData.cameraData.renderType == CameraRenderType.Base &&
               !renderingData.cameraData.xrRendering &&
               !renderingData.cameraData.isPreviewCamera;
    }

    private sealed class GPUOcclusionRenderPass : ScriptableRenderPass
    {
        private static readonly int DepthReduceInputId =
            Shader.PropertyToID("_DepthReduceInput");
        private static readonly int DepthReduceOutputId =
            Shader.PropertyToID("_DepthReduceOutput");
        private static readonly int SourceSizeId =
            Shader.PropertyToID("_SourceSize");
        private static readonly int ZBufferParamsId =
            Shader.PropertyToID("_OcclusionZBufferParams");
        private static readonly int UsesLinearPyramidId =
            Shader.PropertyToID("_UsesLinearDepthPyramid");
        private static readonly int NearClipId =
            Shader.PropertyToID("_NearClip");

        private readonly ComputeShader _shader;
        private readonly Material _occluderDepthMaterial;
        private readonly int _reduceDepthKernel;
        private readonly int _resetHistoryKernel;
        private readonly int _occlusionKernel;
        private readonly int _occlusionThreadGroupSizeX;
        private RenderTexture _sceneOccluderDepth;
        private RenderTexture _depthPyramid;
        private RenderTexture[] _depthMipScratch;
        private int _pyramidWidth;
        private int _pyramidHeight;
        private int _pyramidMipCount;

        private static readonly ShaderTagId UniversalForwardTag =
            new ShaderTagId("UniversalForward");
        private static readonly ShaderTagId UniversalForwardOnlyTag =
            new ShaderTagId("UniversalForwardOnly");
        private static readonly ShaderTagId UniversalGBufferTag =
            new ShaderTagId("UniversalGBuffer");
        private static readonly ShaderTagId SrpDefaultUnlitTag =
            new ShaderTagId("SRPDefaultUnlit");

        public GPUOcclusionRenderPass(ComputeShader shader, Shader occluderDepthShader)
        {
            _shader = shader;
            _occluderDepthMaterial = CoreUtils.CreateEngineMaterial(occluderDepthShader);
            _reduceDepthKernel = shader.FindKernel("ReduceMaxDepth");
            _resetHistoryKernel = shader.FindKernel("ResetHistory");
            _occlusionKernel = shader.FindKernel("OcclusionCull");
            shader.GetKernelThreadGroupSizes(
                _occlusionKernel,
                out uint threadGroupSizeX,
                out _,
                out _);
            _occlusionThreadGroupSizeX = (int)threadGroupSizeX;
            renderPassEvent = RenderPassEvent.AfterRenderingPrePasses;
        }

        [Obsolete("This project intentionally uses URP compatibility mode.", false)]
        public override void Execute(
            ScriptableRenderContext context,
            ref RenderingData renderingData)
        {
            GPUInstanceTracker tracker = GPUInstanceTracker.Instance;
            Camera camera = renderingData.cameraData.camera;
            if (tracker == null ||
                tracker.MainCamera != camera ||
                tracker.OcclusionMode == OcclusionCullingMode.Disabled ||
                !IsCompatibleCamera(ref renderingData))
            {
                return;
            }

            BatchInstancer[] batchers = tracker.GetComponents<BatchInstancer>();
            uint cullingVersion = tracker.MainCameraCullingVersion;
            bool hasPendingBatch = false;
            for (int i = 0; i < batchers.Length; i++)
            {
                if (batchers[i].NeedsOcclusionUpdate(cullingVersion))
                {
                    hasPendingBatch = true;
                    break;
                }
            }

            if (!hasPendingBatch)
                return;

            OcclusionQualitySettings quality =
                GPUOcclusionCulling.GetQualitySettings(tracker.OcclusionMode);
            int width = Mathf.Max(1, renderingData.cameraData.cameraTargetDescriptor.width);
            int height = Mathf.Max(1, renderingData.cameraData.cameraTargetDescriptor.height);
            Vector4 zBufferParams = BuildZBufferParams(camera);
            CommandBuffer cmd = CommandBufferPool.Get("GPU Product Occlusion");
            EnsureSceneOccluderDepth(width, height);
            if (_sceneOccluderDepth == null || !_sceneOccluderDepth.IsCreated())
            {
                AddFrustumFallbackCommands(cmd, batchers);
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
                return;
            }

            RenderSceneOccluderDepth(
                context,
                cmd,
                camera,
                ref renderingData);

            Texture occlusionDepth;
            int mipCount;
            if (quality.buildsDepthPyramid)
            {
                EnsureDepthPyramid(width, height);
                if (_depthPyramid == null || !_depthPyramid.IsCreated())
                {
                    AddFrustumFallbackCommands(cmd, batchers);
                    context.ExecuteCommandBuffer(cmd);
                    CommandBufferPool.Release(cmd);
                    return;
                }

                BuildDepthPyramid(cmd);
                occlusionDepth = _depthPyramid;
                mipCount = _pyramidMipCount;
            }
            else
            {
                occlusionDepth = _sceneOccluderDepth;
                mipCount = 1;
            }
            cmd.SetComputeIntParam(_shader, UsesLinearPyramidId, 1);

            Matrix4x4 viewProjection =
                renderingData.cameraData.GetGPUProjectionMatrix() *
                renderingData.cameraData.GetViewMatrix();
            cmd.SetComputeFloatParam(_shader, NearClipId, camera.nearClipPlane);

            for (int i = 0; i < batchers.Length; i++)
            {
                batchers[i].AddOcclusionCullingCommands(
                    cmd,
                    _shader,
                    _resetHistoryKernel,
                    _occlusionKernel,
                    _occlusionThreadGroupSizeX,
                    occlusionDepth,
                    viewProjection,
                    renderingData.cameraData.GetViewMatrix(),
                    zBufferParams,
                    new Vector2Int(width, height),
                    mipCount,
                    tracker.OcclusionMode,
                    cullingVersion,
                    tracker.OcclusionStateVersion);
            }

            if (quality.buildsDepthPyramid &&
                tracker.ConsumeOcclusionDebugCaptureRequest())
            {
                RecordDepthDebugCapture(
                    cmd,
                    _depthPyramid,
                    _pyramidMipCount,
                    camera.farClipPlane);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private static void AddFrustumFallbackCommands(
            CommandBuffer cmd,
            BatchInstancer[] batchers)
        {
            for (int i = 0; i < batchers.Length; i++)
                batchers[i].AddFrustumFallbackCommands(cmd);
        }

        public void Dispose()
        {
            ReleaseDepthPyramid();

            if (_sceneOccluderDepth != null)
            {
                _sceneOccluderDepth.Release();
                CoreUtils.Destroy(_sceneOccluderDepth);
                _sceneOccluderDepth = null;
            }

            CoreUtils.Destroy(_occluderDepthMaterial);
        }

        private void ReleaseDepthPyramid()
        {
            if (_depthMipScratch != null)
            {
                for (int i = 0; i < _depthMipScratch.Length; i++)
                {
                    if (_depthMipScratch[i] == null)
                        continue;

                    _depthMipScratch[i].Release();
                    CoreUtils.Destroy(_depthMipScratch[i]);
                }
                _depthMipScratch = null;
            }

            if (_depthPyramid != null)
            {
                _depthPyramid.Release();
                CoreUtils.Destroy(_depthPyramid);
                _depthPyramid = null;
            }
            _pyramidWidth = 0;
            _pyramidHeight = 0;
            _pyramidMipCount = 0;
        }

        private void EnsureSceneOccluderDepth(int width, int height)
        {
            if (_sceneOccluderDepth != null &&
                _sceneOccluderDepth.width == width &&
                _sceneOccluderDepth.height == height)
            {
                return;
            }

            if (_sceneOccluderDepth != null)
            {
                _sceneOccluderDepth.Release();
                CoreUtils.Destroy(_sceneOccluderDepth);
            }
            ReleaseDepthPyramid();

            RenderTextureDescriptor descriptor =
                new RenderTextureDescriptor(width, height)
                {
                    graphicsFormat = GraphicsFormat.R32_SFloat,
                    depthBufferBits = 24,
                    msaaSamples = 1,
                    dimension = TextureDimension.Tex2D,
                    volumeDepth = 1,
                    useMipMap = false,
                    autoGenerateMips = false,
                    enableRandomWrite = false,
                    sRGB = false
                };
            _sceneOccluderDepth = new RenderTexture(descriptor)
            {
                name = "GPU Product Scene Occluder Linear Depth",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            _sceneOccluderDepth.Create();
        }

        private void EnsureDepthPyramid(int width, int height)
        {
            if (_depthPyramid != null &&
                _pyramidWidth == width &&
                _pyramidHeight == height)
            {
                return;
            }

            ReleaseDepthPyramid();
            RenderTextureDescriptor descriptor =
                new RenderTextureDescriptor(width, height)
                {
                    graphicsFormat = GraphicsFormat.R32_SFloat,
                    depthBufferBits = 0,
                    msaaSamples = 1,
                    dimension = TextureDimension.Tex2D,
                    volumeDepth = 1,
                    useMipMap = true,
                    autoGenerateMips = false,
                    enableRandomWrite = true,
                    sRGB = false
                };
            _depthPyramid = new RenderTexture(descriptor)
            {
                name = "GPU Product Linear Max Depth Pyramid",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            _depthPyramid.Create();
            _pyramidWidth = width;
            _pyramidHeight = height;
            _pyramidMipCount = _depthPyramid.mipmapCount;

            _depthMipScratch = new RenderTexture[Mathf.Max(0, _pyramidMipCount - 1)];
            for (int mip = 1; mip < _pyramidMipCount; mip++)
            {
                int mipWidth = Mathf.Max(1, width >> mip);
                int mipHeight = Mathf.Max(1, height >> mip);
                RenderTextureDescriptor scratchDescriptor =
                    new RenderTextureDescriptor(mipWidth, mipHeight)
                    {
                        graphicsFormat = GraphicsFormat.R32_SFloat,
                        depthBufferBits = 0,
                        msaaSamples = 1,
                        dimension = TextureDimension.Tex2D,
                        volumeDepth = 1,
                        useMipMap = false,
                        autoGenerateMips = false,
                        enableRandomWrite = true,
                        sRGB = false
                    };
                _depthMipScratch[mip - 1] = new RenderTexture(scratchDescriptor)
                {
                    name = $"GPU Product Max Depth Scratch Mip {mip}",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };
                _depthMipScratch[mip - 1].Create();
            }
        }

        private void RenderSceneOccluderDepth(
            ScriptableRenderContext context,
            CommandBuffer cmd,
            Camera camera,
            ref RenderingData renderingData)
        {
            cmd.BeginSample("Occlusion Scene Depth");
            cmd.SetRenderTarget(_sceneOccluderDepth);
            cmd.SetViewport(new Rect(0f, 0f, _sceneOccluderDepth.width, _sceneOccluderDepth.height));
            cmd.ClearRenderTarget(
                true,
                true,
                new Color(camera.farClipPlane, 0f, 0f, 1f),
                SystemInfo.usesReversedZBuffer ? 0f : 1f);
            cmd.SetViewProjectionMatrices(
                renderingData.cameraData.GetViewMatrix(),
                renderingData.cameraData.GetGPUProjectionMatrix());
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            DrawingSettings drawingSettings = CreateDrawingSettings(
                UniversalForwardTag,
                ref renderingData,
                SortingCriteria.CommonOpaque);
            drawingSettings.SetShaderPassName(1, UniversalForwardOnlyTag);
            drawingSettings.SetShaderPassName(2, UniversalGBufferTag);
            drawingSettings.SetShaderPassName(3, SrpDefaultUnlitTag);
            drawingSettings.overrideMaterial = _occluderDepthMaterial;
            drawingSettings.overrideMaterialPassIndex = 0;
            drawingSettings.perObjectData = PerObjectData.None;

            int occluderLayerMask =
                camera.cullingMask & ~(1 << GPUOcclusionCulling.ProductLayer);
            FilteringSettings filteringSettings =
                new FilteringSettings(RenderQueueRange.opaque, occluderLayerMask);
            context.DrawRenderers(
                renderingData.cullResults,
                ref drawingSettings,
                ref filteringSettings);

            cmd.EndSample("Occlusion Scene Depth");
        }

        private void BuildDepthPyramid(CommandBuffer cmd)
        {
            cmd.BeginSample("Occlusion Depth Pyramid");
            cmd.CopyTexture(
                new RenderTargetIdentifier(_sceneOccluderDepth),
                0,
                0,
                new RenderTargetIdentifier(_depthPyramid),
                0,
                0);

            int sourceWidth = _pyramidWidth;
            int sourceHeight = _pyramidHeight;
            for (int mip = 1; mip < _pyramidMipCount; mip++)
            {
                int destinationWidth = Mathf.Max(1, sourceWidth >> 1);
                int destinationHeight = Mathf.Max(1, sourceHeight >> 1);
                Texture source =
                    mip == 1
                        ? _depthPyramid
                        : _depthMipScratch[mip - 2];
                RenderTexture destination = _depthMipScratch[mip - 1];
                cmd.SetComputeVectorParam(
                    _shader,
                    SourceSizeId,
                    new Vector4(sourceWidth, sourceHeight, destinationWidth, destinationHeight));
                cmd.SetComputeTextureParam(
                    _shader,
                    _reduceDepthKernel,
                    DepthReduceInputId,
                    source);
                cmd.SetComputeTextureParam(
                    _shader,
                    _reduceDepthKernel,
                    DepthReduceOutputId,
                    destination);
                cmd.DispatchCompute(
                    _shader,
                    _reduceDepthKernel,
                    Mathf.CeilToInt(destinationWidth / 8f),
                    Mathf.CeilToInt(destinationHeight / 8f),
                    1);
                cmd.CopyTexture(
                    new RenderTargetIdentifier(destination),
                    0,
                    0,
                    new RenderTargetIdentifier(_depthPyramid),
                    0,
                    mip);
                sourceWidth = destinationWidth;
                sourceHeight = destinationHeight;
            }
            cmd.EndSample("Occlusion Depth Pyramid");
        }

        private static Vector4 BuildZBufferParams(Camera camera)
        {
            float near = camera.nearClipPlane;
            float far = camera.farClipPlane;
            float farOverNear = far / near;
            return SystemInfo.usesReversedZBuffer
                ? new Vector4(
                    farOverNear - 1f,
                    1f,
                    (farOverNear - 1f) / far,
                    1f / far)
                : new Vector4(
                    1f - farOverNear,
                    farOverNear,
                    (1f - farOverNear) / far,
                    farOverNear / far);
        }

        private static void RecordDepthDebugCapture(
            CommandBuffer cmd,
            RenderTexture depthPyramid,
            int mipCount,
            float farClip)
        {
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            string directory = Application.isEditor
                ? Path.GetFullPath(
                    Path.Combine(
                        Application.dataPath,
                        "..",
                        "Library",
                        "GPUOcclusionDebug"))
                : Path.Combine(Application.persistentDataPath, "GPUOcclusionDebug");
            int hierarchyMip = Mathf.Clamp(mipCount / 2, 0, mipCount - 1);

            RecordDepthMipReadback(
                cmd,
                depthPyramid,
                0,
                farClip,
                Path.Combine(directory, $"{timestamp}_linear_depth_mip0.png"));
            if (hierarchyMip > 0)
            {
                RecordDepthMipReadback(
                    cmd,
                    depthPyramid,
                    hierarchyMip,
                    farClip,
                    Path.Combine(
                        directory,
                        $"{timestamp}_linear_max_depth_mip{hierarchyMip}.png"));
            }
        }

        private static void RecordDepthMipReadback(
            CommandBuffer cmd,
            RenderTexture depthPyramid,
            int mip,
            float farClip,
            string path)
        {
            int width = Mathf.Max(1, depthPyramid.width >> mip);
            int height = Mathf.Max(1, depthPyramid.height >> mip);
            cmd.RequestAsyncReadback(
                depthPyramid,
                mip,
                request =>
                {
                    if (request.hasError)
                    {
                        Debug.LogWarning(
                            $"GPU occlusion debug readback failed for mip {mip}.");
                        return;
                    }

                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        var depths = request.GetData<float>();
                        Color32[] pixels = new Color32[width * height];
                        float minimum = float.PositiveInfinity;
                        float maximum = 0f;
                        double sum = 0d;

                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                float depth = depths[y * width + x];
                                minimum = Mathf.Min(minimum, depth);
                                maximum = Mathf.Max(maximum, depth);
                                sum += depth;
                                byte grayscale = (byte)Mathf.RoundToInt(
                                    Mathf.Clamp01(1f - depth / farClip) * 255f);
                                pixels[(height - 1 - y) * width + x] =
                                    new Color32(grayscale, grayscale, grayscale, 255);
                            }
                        }

                        Texture2D image = new Texture2D(
                            width,
                            height,
                            TextureFormat.RGBA32,
                            false,
                            true);
                        image.SetPixels32(pixels);
                        image.Apply(false, false);
                        File.WriteAllBytes(path, image.EncodeToPNG());
                        CoreUtils.Destroy(image);

                        Debug.Log(
                            $"GPU occlusion depth debug saved: {path}; " +
                            $"mip={mip}, size={width}x{height}, " +
                            $"min={minimum:F4}m, max={maximum:F4}m, " +
                            $"average={sum / Mathf.Max(1, depths.Length):F4}m");
                    }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception);
                    }
                });
        }
    }
}
