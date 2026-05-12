using UnityEngine;

/// <summary>
/// Attached to the invisible outline cube that sits over a plain prefab prop (e.g. self-checkout counter).
/// Mirrors ShelfSelector but operates on a raw GameObject instead of a ShelfBuilder.
/// </summary>
public class PropSelector : MonoBehaviour
{
    public GameObject assignedProp;
    public SB_UIHandler uiHandler;
    public SB_InteractionController interactionController;

    private OutlineFx.OutlineFx _outlineFx;
    private Camera _cam;
    private LayerMask _sariInteractableMask;

    void Awake()
    {
        _outlineFx = GetComponent<OutlineFx.OutlineFx>();
        _outlineFx.enabled = false;
        _cam = Camera.main;
        _sariInteractableMask = LayerMask.GetMask("SariInteractable");
    }

    void Update()
    {
        if (interactionController != null && interactionController.IsInPlacementMode) return;

        if (IsSelected() && Input.GetKeyDown(KeyCode.R))
        {
            assignedProp.transform.Rotate(Vector3.up, 90f);
            EncapsulateProp(assignedProp);
            return;
        }

        if (IsSelected() && Input.GetKeyDown(KeyCode.M))
        {
            interactionController.EnterMoveModeForProp(this);
            return;
        }

        if (IsSelected() && Input.GetKeyDown(KeyCode.D))
        {
            interactionController.DuplicateProp(assignedProp);
            return;
        }

        if (!Input.GetMouseButtonDown(0)) return;

        Ray ray = _cam.ScreenPointToRay(Input.mousePosition);
        if (!Physics.Raycast(ray, out RaycastHit hit, 100f, _sariInteractableMask)) return;
        if (hit.collider.gameObject != gameObject) return;

        if (uiHandler.IsActivePropSelector(this))
            uiHandler.DeselectProp();
        else
            uiHandler.SelectProp(this);
    }

    public void Select()   => _outlineFx.enabled = true;
    public void Deselect() => _outlineFx.enabled = false;
    public bool IsSelected() => _outlineFx.enabled;

    public void EncapsulateProp(GameObject prop)
    {
        Bounds bounds = GetCombinedBounds(prop);
        bounds.Expand(0.01f);
        transform.position = bounds.center;
        transform.localScale = bounds.size;
    }

    Bounds GetCombinedBounds(GameObject parent)
    {
        Renderer[] renderers = parent.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return new Bounds(parent.transform.position, Vector3.zero);
        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            b.Encapsulate(renderers[i].bounds);
        return b;
    }
}
