using UnityEngine;

/// <summary>
/// Attached to the invisible outline cube that sits over a shelf.
/// Handles click-to-select / click-to-deselect and drives OutlineFx toggling.
/// </summary>
public class ShelfSelector : OutlineSelector
{
    public ShelfBuilder assignedShelf;

    private LayerMask _sariShelfMask;

    protected override void Awake()
    {
        base.Awake();
        _sariShelfMask = LayerMask.GetMask(StoreBuilderLayers.Shelf);
    }

    protected override void Rotate() => uiHandler.RotateSelectedShelf();
    protected override void Move() => interactionController.EnterMoveMode(this);
    protected override void Duplicate() => interactionController.DuplicateShelf(assignedShelf);

    protected override void OnClicked(Ray ray)
    {
        // If a sub-shelf is under the cursor, let SB_InteractionController handle it
        if (Physics.Raycast(ray, StoreBuilderLayers.RayLength, _sariShelfMask, QueryTriggerInteraction.Ignore)) return;

        if (uiHandler.selectedShelf == assignedShelf)
            uiHandler.DeselectShelf();
        else
            uiHandler.SelectShelf(this);
    }

    public void EncapsulateShelf(ShelfBuilder shelf) => Encapsulate(shelf.gameObject);
}
