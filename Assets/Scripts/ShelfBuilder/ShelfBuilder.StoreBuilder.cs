using UnityEngine;

public partial class ShelfBuilder
{
    /// <summary>The outline box created by <see cref="SummonOutlineBox"/>, if any.</summary>
    public ShelfSelector Selector { get; private set; }

    public void SummonOutlineBox(SB_UIHandler uiHandler, SB_InteractionController interactionController)
    {
        Selector = OutlineSelector.CreateBox<ShelfSelector>(airMaterial, uiHandler, interactionController);
        Selector.assignedShelf = this;
        Selector.EncapsulateShelf(this);
    }
}
