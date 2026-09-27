using UnityEngine;

/// <summary>
/// Attached to the invisible outline cube that sits over a plain prefab prop (e.g. self-checkout counter).
/// Mirrors ShelfSelector but operates on a raw GameObject instead of a ShelfBuilder.
/// </summary>
public class PropSelector : OutlineSelector
{
    public GameObject assignedProp;

    protected override void Rotate()
    {
        assignedProp.transform.Rotate(Vector3.up, 90f);
        EncapsulateProp(assignedProp);
    }

    protected override void Move() => interactionController.EnterMoveModeForProp(this);
    protected override void Duplicate() => interactionController.DuplicateProp(assignedProp);

    protected override void OnClicked(Ray ray)
    {
        if (uiHandler.IsActivePropSelector(this))
            uiHandler.DeselectProp();
        else
            uiHandler.SelectProp(this);
    }

    public void EncapsulateProp(GameObject prop) => Encapsulate(prop);
}
