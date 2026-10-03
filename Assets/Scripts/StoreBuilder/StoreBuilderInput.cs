using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>Store-builder input that ignores keys typed into UI fields or while a dialog is open, and clicks landing on UI.</summary>
public static class StoreBuilderInput
{
    /// <summary>Set by the UI while a modal dialog is open so hotkeys do not reach the scene.</summary>
    public static bool Blocked;

    public static bool Key(KeyCode key) => Input.GetKey(key) && !Suppressed;
    public static bool KeyDown(KeyCode key) => Input.GetKeyDown(key) && !Suppressed;
    public static bool Clicked => Input.GetMouseButtonDown(0) && !PointerOverUI;

    /// <summary>Ctrl or Cmd is held, so plain-letter tool hotkeys stay out of the way (Ctrl+S saves).</summary>
    public static bool Modifier =>
        Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) ||
        Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand);

    public static bool PointerOverUI => EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

    private static bool Suppressed => Blocked || IsTyping;

    private static bool IsTyping
    {
        get
        {
            GameObject selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            return selected != null && selected.TryGetComponent(out TMP_InputField field) && field.isFocused;
        }
    }
}
