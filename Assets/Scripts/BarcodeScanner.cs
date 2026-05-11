using UnityEngine;

/// <summary>
/// Attach to an empty GameObject that also has a BoxCollider (Is Trigger = true).
///
/// Easy   — uses the BoxCollider exactly as the user sized it in the Inspector.
/// Medium — shrinks the BoxCollider to a tighter preset zone at runtime.
/// Hard   — disables the BoxCollider and uses a forward raycast instead.
/// </summary>
[RequireComponent(typeof(AudioSource))]
[RequireComponent(typeof(BoxCollider))]
public class BarcodeScanner : MonoBehaviour
{
    [Header("References")]
    public SelfCheckoutUIHandler checkoutUI;

    [Header("Audio")]
    public AudioClip beepClip;

    [Header("Hard Settings")]
    [Tooltip("Max raycast distance used in Hard difficulty.")]
    public float scanRange = 1.5f;

    // ── Preset box dimensions ─────────────────────────────────────────────────
    // Easy  : whatever the user configured in the Inspector (saved at Start).
    // Medium: tighter fixed zone.
    private static readonly Vector3 MediumCenter = new Vector3(0f,    0f,    0.08f);
    private static readonly Vector3 MediumSize   = new Vector3(0.27f, 0.04f, 0.13f);

    // ── Runtime state ─────────────────────────────────────────────────────────
    private AudioSource       audioSource;
    private BoxCollider       boxCollider;
    private Vector3           easyCenter;
    private Vector3           easySize;
    private ScanningDifficulty lastAppliedDifficulty;
    private bool              scanCooldown;

    // ─────────────────────────────────────────────────────────────────────────

    void Awake()
    {
        audioSource = GetComponent<AudioSource>();
        boxCollider = GetComponent<BoxCollider>();
        boxCollider.isTrigger = true;

        // Capture whatever center/size the user set in the Inspector as the Easy preset.
        easyCenter = boxCollider.center;
        easySize   = boxCollider.size;
    }

    void Start()
    {
        // Apply the correct collider configuration for the starting difficulty.
        if (DataHandler.Instance != null)
            ApplyDifficulty(DataHandler.Instance.scanningDifficulty);
    }

    void Update()
    {
        if (DataHandler.Instance == null || checkoutUI == null) return;

        ScanningDifficulty current = DataHandler.Instance.scanningDifficulty;

        // Re-apply if difficulty was changed at runtime.
        if (current != lastAppliedDifficulty)
            ApplyDifficulty(current);

        // Hard mode: raycast every frame.
        if (current == ScanningDifficulty.Hard)
            RaycastScan();
    }

    // ─── Trigger scanning (Easy + Medium) ─────────────────────────────────────

    // OnTriggerEnter is the correct callback: Unity fires it on the frame a
    // trigger collider first overlaps another collider.
    void OnTriggerEnter(Collider other)
    {
        if (DataHandler.Instance == null || checkoutUI == null) return;
        if (DataHandler.Instance.scanningDifficulty == ScanningDifficulty.Hard) return;
        if (scanCooldown) return;

        if (other is MeshCollider && other.CompareTag("Barcode"))
            RegisterScan(other.transform.parent.gameObject.name);
    }

    // ─── Raycast scanning (Hard) ───────────────────────────────────────────────

    private void RaycastScan()
    {
        Ray ray = new Ray(transform.position, transform.forward);

        // Visualise the ray in the Scene view (and Game view with Gizmos on).
        Debug.DrawRay(
            ray.origin,
            ray.direction * scanRange,
            Color.red
        );

        if (!Physics.Raycast(ray, out RaycastHit hit, scanRange)) return;

        if (hit.collider is MeshCollider && hit.collider.CompareTag("Barcode"))
            RegisterScan(hit.collider.transform.parent.gameObject.name);
    }

    // ─── Shared helpers ────────────────────────────────────────────────────────

    private void RegisterScan(string itemId)
    {
        checkoutUI.AddScannedItem(itemId);
        PlayBeep();
        StartCoroutine(ScanCooldown());
    }

    private void PlayBeep()
    {
        if (beepClip != null)
            audioSource.PlayOneShot(beepClip);
    }

    private System.Collections.IEnumerator ScanCooldown()
    {
        scanCooldown = true;
        yield return new WaitForSeconds(0.5f);
        scanCooldown = false;
    }

    // ─── Difficulty application ────────────────────────────────────────────────

    private void ApplyDifficulty(ScanningDifficulty difficulty)
    {
        switch (difficulty)
        {
            case ScanningDifficulty.Easy:
                boxCollider.enabled = true;
                boxCollider.center  = easyCenter;
                boxCollider.size    = easySize;
                break;

            case ScanningDifficulty.Medium:
                boxCollider.enabled = true;
                boxCollider.center  = MediumCenter;
                boxCollider.size    = MediumSize;
                break;

            case ScanningDifficulty.Hard:
                // Collider not needed; raycast handles detection.
                boxCollider.enabled = false;
                break;
        }

        lastAppliedDifficulty = difficulty;
    }
}
