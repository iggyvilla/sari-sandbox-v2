using System;
using System.Collections;
using UnityEngine;

public static class ScreenshotUtility
{
    // Delay so the frame captures movement that already happened; real time so a paused simulation cannot hang it.
    private const float SettleDelaySeconds = 0.5f;

    public static IEnumerator GetScreenshotBase64(Action<string> callback)
    {
        yield return GetScreenshotBytes(bytes => callback?.Invoke(Convert.ToBase64String(bytes)));
    }

    public static IEnumerator GetScreenshotBytes(Action<byte[]> callback)
    {
        yield return new WaitForSecondsRealtime(SettleDelaySeconds);

        yield return new WaitForEndOfFrame();

        Texture2D screenshot = ScreenCapture.CaptureScreenshotAsTexture();
        byte[] bytes = screenshot.EncodeToPNG();
        UnityEngine.Object.Destroy(screenshot);

        callback?.Invoke(bytes);
    }

    public static IEnumerator GetScreenshotBytes(
        Camera cam,
        Action<byte[]> callback,
        Action beforeRender = null,
        Action afterRender = null,
        Func<bool> isCancelled = null)
    {
        // Renders the camera into its own RenderTexture, so it does not depend on the end-of-frame callback.
        yield return new WaitForSecondsRealtime(SettleDelaySeconds);

        if (isCancelled?.Invoke() == true) yield break;
        if (cam == null) yield break;

        RenderTexture rt = RenderTexture.GetTemporary(Screen.width, Screen.height, 24);
        RenderTexture prevTarget = cam.targetTexture;

        try
        {
            beforeRender?.Invoke();
            cam.targetTexture = rt;
            cam.Render();

            callback?.Invoke(RenderTextureUtility.EncodeToPng(rt, TextureFormat.RGB24));
        }
        finally
        {
            cam.targetTexture = prevTarget;
            afterRender?.Invoke();
            RenderTexture.ReleaseTemporary(rt);
        }
    }
}
