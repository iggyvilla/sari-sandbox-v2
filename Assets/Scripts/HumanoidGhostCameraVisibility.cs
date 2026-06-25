using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(Camera))]
public class HumanoidGhostCameraVisibility : MonoBehaviour
{
    private Camera _ownerCamera;
    private HumanoidGhostFollower _ghost;
    private bool _usesScriptableRenderPipeline;
    private int _suppressionDepth;

    public HumanoidGhostFollower Ghost => _ghost;

    public void Bind(HumanoidGhostFollower ghost)
    {
        RestoreVisibility();
        _ownerCamera = GetComponent<Camera>();
        _ghost = ghost;
        _usesScriptableRenderPipeline = GraphicsSettings.currentRenderPipeline != null;
    }

    private void OnEnable()
    {
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
    }

    private void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        RestoreVisibility();
    }

    private void OnPreCull()
    {
        if (!_usesScriptableRenderPipeline) SuppressVisibility();
    }

    private void OnPostRender()
    {
        if (!_usesScriptableRenderPipeline) RestoreOneSuppression();
    }

    private void OnBeginCameraRendering(ScriptableRenderContext context, Camera renderingCamera)
    {
        if (_usesScriptableRenderPipeline && renderingCamera == _ownerCamera)
            SuppressVisibility();
    }

    private void OnEndCameraRendering(ScriptableRenderContext context, Camera renderingCamera)
    {
        if (_usesScriptableRenderPipeline && renderingCamera == _ownerCamera)
            RestoreOneSuppression();
    }

    private void SuppressVisibility()
    {
        if (_ghost == null) return;

        _suppressionDepth++;
        _ghost.SetRenderersVisible(false);
    }

    private void RestoreOneSuppression()
    {
        if (_ghost == null || _suppressionDepth <= 0) return;

        _suppressionDepth--;
        _ghost.SetRenderersVisible(true);
    }

    private void RestoreVisibility()
    {
        while (_ghost != null && _suppressionDepth > 0)
            RestoreOneSuppression();
    }
}
