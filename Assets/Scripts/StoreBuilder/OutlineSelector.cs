using UnityEngine;

/// <summary>Layer names and ray length shared by the store builder's raycasts.</summary>
public static class StoreBuilderLayers
{
    public const string Interactable = "SariInteractable";
    public const string Shelf = "SariShelf";
    public const string Floor = "SariFloor";
    public const float RayLength = 100f;
}

/// <summary>
/// Invisible outline box over a store-builder object. Click toggles selection; while selected,
/// R/M/D rotate, move and duplicate the object.
/// </summary>
public abstract class OutlineSelector : MonoBehaviour
{
    public SB_UIHandler uiHandler;
    public SB_InteractionController interactionController;

    private OutlineFx.OutlineFx _outlineFx;
    private Camera _cam;

    // One click raycast per frame, shared by every selector.
    private static int s_clickFrame = -1;
    private static Collider s_clickedCollider;

    protected virtual void Awake()
    {
        _outlineFx = GetComponent<OutlineFx.OutlineFx>();
        _outlineFx.enabled = false;
        _cam = Camera.main;
    }

    void Update()
    {
        if (interactionController != null && interactionController.IsInPlacementMode) return;
        if (!uiHandler.interactionControlsEnabled) return;

        if (IsSelected())
        {
            if (Input.GetKeyDown(KeyCode.R)) { Rotate(); return; }
            if (Input.GetKeyDown(KeyCode.M)) { Move(); return; }
            if (Input.GetKeyDown(KeyCode.D)) { Duplicate(); return; }
        }

        if (!Input.GetMouseButtonDown(0)) return;

        Ray ray = _cam.ScreenPointToRay(Input.mousePosition);
        Collider clicked = ClickedCollider(ray);
        if (clicked == null || clicked.gameObject != gameObject) return;
        OnClicked(ray);
    }

    protected abstract void Rotate();
    protected abstract void Move();
    protected abstract void Duplicate();
    protected abstract void OnClicked(Ray ray);

    public void Select() => _outlineFx.enabled = true;
    public void Deselect() => _outlineFx.enabled = false;
    public bool IsSelected() => _outlineFx.enabled;

    /// <summary>Fits this box around every renderer under <paramref name="target"/>.</summary>
    protected void Encapsulate(GameObject target)
    {
        Bounds bounds = GetCombinedBounds(target);
        bounds.Expand(0.01f);
        transform.position = bounds.center;
        transform.localScale = bounds.size;
    }

    /// <summary>Creates a trigger cube on the interactable layer carrying a selector of type T.</summary>
    public static T CreateBox<T>(
        Material material,
        SB_UIHandler uiHandler,
        SB_InteractionController interactionController) where T : OutlineSelector
    {
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.layer = LayerMask.NameToLayer(StoreBuilderLayers.Interactable);
        cube.GetComponent<Renderer>().sharedMaterial = material;
        // Keep selection bounds raycastable without interfering with physics.
        cube.GetComponent<BoxCollider>().isTrigger = true;
        cube.AddComponent<OutlineFx.OutlineFx>();

        T selector = cube.AddComponent<T>();
        selector.uiHandler = uiHandler;
        selector.interactionController = interactionController;
        return selector;
    }

    private static Collider ClickedCollider(Ray ray)
    {
        if (s_clickFrame == Time.frameCount) return s_clickedCollider;

        s_clickFrame = Time.frameCount;
        s_clickedCollider = Physics.Raycast(
            ray,
            out RaycastHit hit,
            StoreBuilderLayers.RayLength,
            LayerMask.GetMask(StoreBuilderLayers.Interactable))
            ? hit.collider
            : null;
        return s_clickedCollider;
    }

    private static Bounds GetCombinedBounds(GameObject parent)
    {
        Renderer[] renderers = parent.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return new Bounds(parent.transform.position, Vector3.zero);

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            bounds.Encapsulate(renderers[i].bounds);
        return bounds;
    }
}
