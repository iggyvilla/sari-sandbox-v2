using UnityEngine;

/// <summary>
/// CPU readback helpers for render textures (debug captures, screenshots, sprite bakes).
/// </summary>
public static class RenderTextureUtility
{
    /// <summary>
    /// Copies a render texture into a new readable Texture2D. The caller owns (and must destroy) the result.
    /// </summary>
    public static Texture2D ReadToTexture(RenderTexture source, TextureFormat format)
    {
        RenderTexture previous = RenderTexture.active;
        Texture2D texture = new Texture2D(source.width, source.height, format, false);

        try
        {
            RenderTexture.active = source;
            texture.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            texture.Apply();
            return texture;
        }
        catch
        {
            DestroyTexture(texture);
            throw;
        }
        finally
        {
            RenderTexture.active = previous;
        }
    }

    public static byte[] EncodeToPng(RenderTexture source, TextureFormat format)
    {
        Texture2D texture = ReadToTexture(source, format);
        try
        {
            return texture.EncodeToPNG();
        }
        finally
        {
            DestroyTexture(texture);
        }
    }

    // Edit-mode callers (editor bakers) cannot use deferred Destroy.
    public static void DestroyTexture(Object texture)
    {
        if (texture == null) return;

        if (Application.isPlaying)
            Object.Destroy(texture);
        else
            Object.DestroyImmediate(texture);
    }
}
