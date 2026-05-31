using UnityEngine;

public partial class ShelfBuilder
{
    public void SummonOutlineBox(SB_UIHandler uiHandler, SB_InteractionController interactionController)
    {
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.layer = LayerMask.NameToLayer("SariInteractable");
        cube.GetComponent<Renderer>().sharedMaterial = airMaterial;

        // Keep selection bounds raycastable without interfering with physics.
        cube.GetComponent<BoxCollider>().isTrigger = true;
        cube.AddComponent<OutlineFx.OutlineFx>();

        ShelfSelector selector = cube.AddComponent<ShelfSelector>();
        selector.assignedShelf = this;
        selector.uiHandler = uiHandler;
        selector.interactionController = interactionController;
        selector.EncapsulateShelf(this);
    }
}
