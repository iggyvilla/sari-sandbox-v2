using UnityEngine;

/// <summary>Vector3 &lt;-&gt; float[3] conversion for the JSON wire protocol.</summary>
public static class WireVec
{
    public static float[] ToArray(Vector3 v) => new[] { v.x, v.y, v.z };

    /// <summary>Missing or short arrays read as zero, matching JsonUtility's absent-field behavior.</summary>
    public static Vector3 ToVector3(float[] arr) =>
        arr == null || arr.Length < 3 ? Vector3.zero : new Vector3(arr[0], arr[1], arr[2]);
}
