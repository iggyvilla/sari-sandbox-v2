using UnityEngine;

/// <summary>
/// Attached to the invisible outline cube that sits over a shelf.
/// Handles click-to-select / click-to-deselect and drives OutlineFx toggling.
/// </summary>
public class ShelfSelector : MonoBehaviour
{
    public ShelfBuilder assignedShelf;
    public StoreBuilderUIHandler uiHandler;
    public StoreBuilderCameraController cameraController;

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
        if (!Input.GetMouseButtonDown(0)) return;
        if (cameraController != null && cameraController.IsInPlacementMode) return;

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

    // Called by StoreBuilderUIHandler when this selector is deactivated
    public void Deselect()
    {
        _outlineFx.enabled = false;
    }
}
