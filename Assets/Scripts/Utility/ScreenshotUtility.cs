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

    public static IEnumerator GetScreenshotBase64(Camera cam, Action<string> callback)
    {
        yield return new WaitForSeconds(0.5f);
        yield return new WaitForEndOfFrame();

        RenderTexture rt = new RenderTexture(Screen.width, Screen.height, 24);
        RenderTexture prevTarget = cam.targetTexture;
        cam.targetTexture = rt;
        cam.Render();
        cam.targetTexture = prevTarget;

        RenderTexture.active = rt;
        Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = null;
        UnityEngine.Object.Destroy(rt);

        byte[] bytes = tex.EncodeToPNG();
        string base64 = Convert.ToBase64String(bytes);
        UnityEngine.Object.Destroy(tex);

        callback?.Invoke(base64);
    }
}
