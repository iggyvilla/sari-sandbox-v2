using System;
using System.Collections;
using UnityEngine;

public static class ScreenshotUtility
{
    public static IEnumerator GetScreenshotBase64(Action<string> callback)
    {
        // Delay so the frame captures movement that already happened
        yield return new WaitForSeconds(0.5f);

        yield return new WaitForEndOfFrame();

        Texture2D screenshot = ScreenCapture.CaptureScreenshotAsTexture();
        byte[] bytes = screenshot.EncodeToPNG();
        string base64 = Convert.ToBase64String(bytes);
        UnityEngine.Object.Destroy(screenshot);

        callback?.Invoke(base64);
    }

    public static IEnumerator GetScreenshotBase64(
        Camera cam,
        Action<string> callback,
        Action beforeRender = null,
        Action afterRender = null)
    {
        yield return new WaitForSeconds(0.5f);
        yield return new WaitForEndOfFrame();

        if (cam == null) yield break;

        RenderTexture rt = new RenderTexture(Screen.width, Screen.height, 24);
        RenderTexture prevTarget = cam.targetTexture;
        RenderTexture prevActive = RenderTexture.active;
        Texture2D tex = null;

        try
        {
            beforeRender?.Invoke();
            cam.targetTexture = rt;
            cam.Render();

            RenderTexture.active = rt;
            tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();

            byte[] bytes = tex.EncodeToPNG();
            callback?.Invoke(Convert.ToBase64String(bytes));
        }
        finally
        {
            cam.targetTexture = prevTarget;
            RenderTexture.active = prevActive;
            afterRender?.Invoke();
            UnityEngine.Object.Destroy(rt);
            if (tex != null) UnityEngine.Object.Destroy(tex);
        }
    }
}
