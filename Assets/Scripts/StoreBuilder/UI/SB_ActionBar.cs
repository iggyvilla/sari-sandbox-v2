using UnityEngine;
using UnityEngine.UI;

/// <summary>Move / Rotate / Copy / Delete buttons of an inspector; the controller does the work.</summary>
public class SB_ActionBar : MonoBehaviour
{
    public Button move;
    public Button rotate;
    public Button duplicate;
    public Button delete;

    public void Bind(SB_InteractionController controller)
    {
        move.onClick.AddListener(controller.MoveSelected);
        rotate.onClick.AddListener(controller.RotateSelected);
        duplicate.onClick.AddListener(controller.DuplicateSelected);
        delete.onClick.AddListener(controller.DeleteSelected);
    }
}
