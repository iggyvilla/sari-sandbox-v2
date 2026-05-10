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
}
