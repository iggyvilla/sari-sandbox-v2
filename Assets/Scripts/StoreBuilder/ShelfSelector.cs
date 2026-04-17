using UnityEngine;

/// <summary>
/// Attached to the invisible outline cube that sits over a shelf.
/// Handles click-to-select / click-to-deselect and drives OutlineFx toggling.
/// </summary>
public class ShelfSelector : MonoBehaviour
{
    public ShelfBuilder assignedShelf;
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

        if (IsShelfSelected() && Input.GetKeyDown(KeyCode.R))
        {
            uiHandler.RotateSelectedShelf();
            return;
        }

        if (IsShelfSelected() && Input.GetKeyDown(KeyCode.M))
        {
            interactionController.EnterMoveMode(this);
            return;
        }

        if (IsShelfSelected() && Input.GetKeyDown(KeyCode.D))
        {
            interactionController.DuplicateShelf(assignedShelf);
            return;
        }

        if (!Input.GetMouseButtonDown(0)) return;

        Ray ray = _cam.ScreenPointToRay(Input.mousePosition);
        if (!Physics.Raycast(ray, out RaycastHit hit, 100f, _sariInteractableMask)) return;
        if (hit.collider.gameObject != gameObject) return;

        if (uiHandler.selectedShelf == assignedShelf)
            uiHandler.DeselectShelf();
        else
            uiHandler.SelectShelf(this);
    }

    // Called by StoreBuilderUIHandler when this selector becomes active
    public void Select()
    {
        _outlineFx.enabled = true;
    }

    public void EncapsulateShelf(ShelfBuilder shelf)
    {
        Bounds totalBounds = GetCombinedBounds(shelf.gameObject);
        totalBounds.Expand(0.01f);
        
        // Match the center and size of the shelf's combined bounds
        transform.position = totalBounds.center;
        transform.localScale = totalBounds.size;
    }
    
    Bounds GetCombinedBounds(GameObject parent)
    {
        // Get all renderers in children (including the parent if it has one)
        Renderer[] renderers = parent.GetComponentsInChildren<Renderer>();

        if (renderers.Length == 0) return new Bounds(parent.transform.position, Vector3.zero);

        // Initialize bounds with the first renderer found
        Bounds combinedBounds = renderers[0].bounds;

        // Expand the bounds to include every other renderer
        for (int i = 1; i < renderers.Length; i++)
        {
            combinedBounds.Encapsulate(renderers[i].bounds);
        }

        return combinedBounds;
    }

    public bool IsShelfSelected()
    {
        return _outlineFx.enabled;
    }

    // Called by StoreBuilderUIHandler when this selector is deactivated
    public void Deselect()
    {
        _outlineFx.enabled = false;
    }
}
