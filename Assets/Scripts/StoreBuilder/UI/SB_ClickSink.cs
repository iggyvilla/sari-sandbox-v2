using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Swallows clicks inside a dialog window. uGUI fires a click on the nearest click handler where a press started, even
/// after a drag (a scrollbar drag released over empty space), so without this the dialog's scrim would close it.
/// </summary>
public class SB_ClickSink : MonoBehaviour, IPointerClickHandler
{
    public void OnPointerClick(PointerEventData eventData) { }
}
