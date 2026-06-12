using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

// Verification harness for the Hi-Z occlusion assumptions (A1–A7, see HiZOcclusionManager).
// Auto-added next to GPUInstanceTracker. Hotkeys (also in the component context menu):
//   F9  — dump every Hi-Z mip as PNGs (raw reversed-Z + linearized) → <project>/HiZDebug/
//         A1: non-black. A2: raw dump near=white/far=black. A3: same orientation as a screenshot.
//   F10 — per-instance occlusion records from the largest batch (A3/A4): projected UV, mip,
//         sphere depth vs sampled occluder depth, verdict.
//   F11 — A/B visible-count comparison, occlusion off vs on (A6/A7): on-counts must be ≤ off-counts.
public class OcclusionDebugger : MonoBehaviour
{
    public static OcclusionDebugger Instance { get; private set; }

    private const int MaxDebugRecords = 64;
    private const uint ResultSentinel = 0xFFFFFFFF;

    // Must match DebugRecord in FrustumCullingFilterer.compute (stride 28).
    [StructLayout(LayoutKind.Sequential)]
    private struct DebugRecord
    {
        public uint instanceIndex;
        public float u, v;
        public uint mip;
        public float sphereDepth;
        public float hizSample;
        public uint result;
    }

    private static readonly string[] ResultNames =
        { "VISIBLE (tested)", "OCCLUDED", "frustum-culled", "near-plane skip", "not tested" };

    private bool _routineRunning;

    void Awake()
    {
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.V)) DumpHiZMips();
        if (Input.GetKeyDown(KeyCode.B)) CaptureInstanceRecords();
        if (Input.GetKeyDown(KeyCode.N)) RunABCountTest();
    }

    // ---------- A1/A2/A3: depth-map dumps ----------

    /// <summary>
    /// PNG of one Hi-Z mip. Uses the same RT→ReadPixels→EncodeToPNG flow as ScreenshotUtility,
    /// so its orientation is directly comparable to a RequestScreenshot image — that comparison
    /// IS the A3 test. normalized=true remaps to linear eye depth (near=white, far=black);
    /// normalized=false shows the raw reversed-Z values (the A2 test).
    /// </summary>
    public byte[] GetDepthMapPNG(int mip = 0, bool normalized = true)
    {
        HiZOcclusionManager hiz = HiZOcclusionManager.Instance;
        if (hiz == null || hiz.HiZTexture == null) return null;

        int w = Mathf.Max(1, hiz.HiZTexture.width >> mip);
        int h = Mathf.Max(1, hiz.HiZTexture.height >> mip);

        RenderTexture tmp = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.RFloat);
        Graphics.CopyTexture(hiz.HiZTexture, 0, mip, 0, 0, w, h, tmp, 0, 0, 0, 0);

        RenderTexture prevActive = RenderTexture.active;
        Texture2D depthTex = new Texture2D(w, h, TextureFormat.RFloat, false);
        try
        {
            RenderTexture.active = tmp;
            depthTex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            depthTex.Apply();
        }
        finally
        {
            RenderTexture.active = prevActive;
            RenderTexture.ReleaseTemporary(tmp);
        }

        Color[] src = depthTex.GetPixels();
        Color[] dst = new Color[src.Length];
        float near = Mathf.Max(hiz.CapturedNear, 1e-4f);
        float far = Mathf.Max(hiz.CapturedFar, near + 1e-3f);
        for (int i = 0; i < src.Length; i++)
        {
            float d = src[i].r; // raw reversed-Z: 1 = near, 0 = far (assumption A2)
            float g;
            if (normalized)
            {
                // Linear eye depth from reversed-Z, remapped so near = white, far = black
                float eye = 1f / ((1f / near - 1f / far) * d + 1f / far);
                g = 1f - Mathf.Clamp01((eye - near) / (far - near));
            }
            else
            {
                g = Mathf.Clamp01(d);
            }
            dst[i] = new Color(g, g, g);
        }

        Texture2D outTex = new Texture2D(w, h, TextureFormat.RGB24, false);
        outTex.SetPixels(dst);
        outTex.Apply();
        byte[] png = outTex.EncodeToPNG();
        Destroy(depthTex);
        Destroy(outTex);
        return png;
    }

    [ContextMenu("Dump HiZ Mips To PNG (F9)")]
    public void DumpHiZMips()
    {
        HiZOcclusionManager hiz = HiZOcclusionManager.Instance;
        if (hiz == null || hiz.HiZTexture == null)
        {
            Debug.LogWarning("OcclusionDebugger: no Hi-Z texture yet (A1 not satisfied — has a depth capture happened?)");
            return;
        }

        string dir = Application.isEditor
            ? Path.GetFullPath(Path.Combine(Application.dataPath, "..", "HiZDebug"))
            : Path.Combine(Application.persistentDataPath, "HiZDebug");
        Directory.CreateDirectory(dir);
        Debug.Log(dir);

        // The raw depth copy that feeds mip 0 — the direct "is _CameraDepthTexture readable
        // and populated" test, before any pyramid math can interfere.
        if (hiz.DepthSourceRT != null)
        {
            DumpRFloatRT(hiz.DepthSourceRT, Path.Combine(dir, "hiz_source_raw.png"), false, hiz.CapturedNear, hiz.CapturedFar);
            DumpRFloatRT(hiz.DepthSourceRT, Path.Combine(dir, "hiz_source_linear.png"), true, hiz.CapturedNear, hiz.CapturedFar);
        }
        else
        {
            Debug.LogWarning("OcclusionDebugger: DepthSourceRT is null — the depth copy pass has not run.");
        }

        for (int mip = 0; mip < hiz.HiZMipCount; mip++)
        {
            byte[] raw = GetDepthMapPNG(mip, normalized: false);
            byte[] norm = GetDepthMapPNG(mip, normalized: true);
            if (raw != null) File.WriteAllBytes(Path.Combine(dir, $"hiz_mip{mip}_raw.png"), raw);
            if (norm != null) File.WriteAllBytes(Path.Combine(dir, $"hiz_mip{mip}_linear.png"), norm);
        }

        Debug.Log($"OcclusionDebugger: dumped {hiz.HiZMipCount} mips to {dir}\n" +
                  "Checks — A1: images non-black. A2: in *_raw, near shelves WHITE, far wall/sky BLACK " +
                  "(if inverted, reversed-Z assumption is wrong). A3: orientation matches a normal " +
                  "screenshot (if vertically mirrored, toggle flipY on HiZOcclusionManager).");
    }

    /// <summary>Reads an RFloat RT and writes it as a grayscale PNG (raw or linearized depth).</summary>
    private static void DumpRFloatRT(RenderTexture rt, string path, bool normalized, float near, float far)
    {
        RenderTexture prevActive = RenderTexture.active;
        Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RFloat, false);
        try
        {
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
        }
        finally
        {
            RenderTexture.active = prevActive;
        }

        near = Mathf.Max(near, 1e-4f);
        far = Mathf.Max(far, near + 1e-3f);
        Color[] src = tex.GetPixels();
        Color[] dst = new Color[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            float d = src[i].r;
            float g;
            if (normalized)
            {
                float eye = 1f / ((1f / near - 1f / far) * d + 1f / far);
                g = 1f - Mathf.Clamp01((eye - near) / (far - near));
            }
            else
            {
                g = Mathf.Clamp01(d);
            }
            dst[i] = new Color(g, g, g);
        }

        Texture2D outTex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        outTex.SetPixels(dst);
        outTex.Apply();
        File.WriteAllBytes(path, outTex.EncodeToPNG());
        Destroy(tex);
        Destroy(outTex);
    }

    // ---------- A3/A4: per-instance records ----------

    [ContextMenu("Capture Per-Instance Records (F10)")]
    public void CaptureInstanceRecords()
    {
        if (!_routineRunning) StartCoroutine(InstanceRecordRoutine());
    }

    private IEnumerator InstanceRecordRoutine()
    {
        _routineRunning = true;
        try
        {
            BatchInstancer target = FindLargestInstancer();
            if (target == null)
            {
                Debug.LogWarning("OcclusionDebugger: no BatchInstancer with instances found.");
                yield break;
            }

            int n = Mathf.Min(MaxDebugRecords, target.InstanceCount);
            ComputeBuffer buf = new ComputeBuffer(Mathf.Max(n, 1), Marshal.SizeOf<DebugRecord>());
            DebugRecord[] records = new DebugRecord[n];
            for (int i = 0; i < n; i++) records[i].result = ResultSentinel;
            buf.SetData(records);

            ComputeShader shader = target.frustumCullingShader;
            shader.EnableKeyword("OCCLUSION_DEBUG");
            shader.SetBuffer(target.CullingKernel, "debug_records", buf);
            shader.SetInt("debug_record_count", n);

            // Skip the rest of this frame (some instancers already dispatched before the
            // keyword flip), then let one full Update() pass run with the debug variant
            yield return null;
            yield return new WaitForEndOfFrame();

            buf.GetData(records);
            shader.DisableKeyword("OCCLUSION_DEBUG");
            buf.Release();

            HiZOcclusionManager hiz = HiZOcclusionManager.Instance;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"OcclusionDebugger: records for '{target.itemId}' " +
                          $"(occlusionEnabled={hiz != null && hiz.OcclusionEnabled}, valid={hiz != null && hiz.IsValid}, " +
                          $"flipY={hiz != null && hiz.FlipY}):");
            sb.AppendLine("idx | uv (hi-z space)  | mip | sphereDepth | hizSample | verdict");
            foreach (DebugRecord r in records)
            {
                string verdict = r.result == ResultSentinel ? "thread never ran (?)"
                    : r.result < ResultNames.Length ? ResultNames[r.result] : $"unknown ({r.result})";
                sb.AppendLine($"{r.instanceIndex,3} | ({r.u:F3}, {r.v:F3})   | {r.mip,3} | {r.sphereDepth:F5}     | {r.hizSample:F5}   | {verdict}");
            }
            sb.AppendLine("Checks — A3: for an item visible on screen, uv must match its screen position " +
                          "(u: 0=left..1=right; v compare against the F9 dump orientation). " +
                          "A4: visible items have sphereDepth in (0,1) and sphereDepth+bias >= hizSample; " +
                          "uv outside [0,1] for on-screen items means the VP matrix is wrong.");
            Debug.Log(sb.ToString());
        }
        finally
        {
            _routineRunning = false;
        }
    }

    // ---------- A6/A7: A/B visible-count comparison ----------

    [ContextMenu("Run A/B Count Test (F11)")]
    public void RunABCountTest()
    {
        if (!_routineRunning) StartCoroutine(ABCountRoutine());
    }

    private IEnumerator ABCountRoutine()
    {
        _routineRunning = true;
        HiZOcclusionManager hiz = HiZOcclusionManager.Instance;
        GPUInstanceTracker tracker = GPUInstanceTracker.Instance;
        if (hiz == null || tracker == null)
        {
            Debug.LogWarning("OcclusionDebugger: HiZOcclusionManager / GPUInstanceTracker missing.");
            _routineRunning = false;
            yield break;
        }

        bool prevEnabled = hiz.occlusionEnabled;
        try
        {
            // Each toggle waits a full frame: instancers that dispatched earlier in the toggle
            // frame still used the old setting, so reading that frame would mix both modes
            hiz.occlusionEnabled = false;
            yield return null;
            yield return new WaitForEndOfFrame();
            Dictionary<string, int[]> off = CollectCounts(tracker);

            hiz.occlusionEnabled = true;
            yield return null;
            yield return new WaitForEndOfFrame();
            Dictionary<string, int[]> on = CollectCounts(tracker);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"OcclusionDebugger A/B (hiZ valid={hiz.IsValid}; keep the camera still while running this):");
            sb.AppendLine("itemId | lod | off | on | culled-by-occlusion");
            int totalOff = 0, totalOn = 0, violations = 0;
            foreach (KeyValuePair<string, int[]> kvp in off)
            {
                int[] onCounts = on.TryGetValue(kvp.Key, out int[] oc) ? oc : null;
                for (int lod = 0; lod < kvp.Value.Length; lod++)
                {
                    int offC = kvp.Value[lod];
                    int onC = onCounts != null && lod < onCounts.Length ? onCounts[lod] : 0;
                    totalOff += offC;
                    totalOn += onC;
                    if (onC > offC) violations++;
                    if (offC != 0 || onC != 0)
                        sb.AppendLine($"{kvp.Key} | {lod} | {offC} | {onC} | {offC - onC}{(onC > offC ? "  <-- A7 VIOLATION" : "")}");
                }
            }
            sb.AppendLine($"TOTAL: off={totalOff}, on={totalOn}, culled={totalOff - totalOn}");
            if (violations > 0)
                sb.AppendLine($"A7 FAILED: {violations} buffers grew with occlusion on. If the camera was " +
                              "moving, re-run while stationary; otherwise the occlusion test is wrong.");
            else if (totalOff == totalOn)
                sb.AppendLine("A7 holds but nothing was culled — either nothing is occluded from this view, " +
                              "or the test never rejects (check F10 records and the F9 dumps).");
            else
                sb.AppendLine("A7 holds: occlusion-on visible set is a subset (A6/A7 OK from this viewpoint).");
            Debug.Log(sb.ToString());
        }
        finally
        {
            hiz.occlusionEnabled = prevEnabled;
            _routineRunning = false;
        }
    }

    private static Dictionary<string, int[]> CollectCounts(GPUInstanceTracker tracker)
    {
        Dictionary<string, int[]> result = new();
        ComputeBuffer staging = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Raw);
        int[] count = new int[1];
        try
        {
            foreach (BatchInstancer bi in tracker.AllInstancers)
            {
                int[] counts = new int[bi.ActiveLodCount];
                for (int lod = 0; lod < counts.Length; lod++)
                {
                    ComputeBuffer buf = bi.GetVisibleIndexBuffer(lod);
                    if (buf == null) continue;
                    ComputeBuffer.CopyCount(buf, staging, 0);
                    staging.GetData(count); // sync readback — debug only
                    counts[lod] = count[0];
                }
                result[bi.itemId ?? "?"] = counts;
            }
        }
        finally
        {
            staging.Release();
        }
        return result;
    }

    private static BatchInstancer FindLargestInstancer()
    {
        BatchInstancer best = null;
        if (GPUInstanceTracker.Instance == null) return null;
        foreach (BatchInstancer bi in GPUInstanceTracker.Instance.AllInstancers)
            if (bi.InstanceCount > 0 && (best == null || bi.InstanceCount > best.InstanceCount))
                best = bi;
        return best;
    }
}
