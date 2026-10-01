using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>Store-builder input that ignores keys typed into UI fields and clicks landing on UI.</summary>
public static class StoreBuilderInput
{
    public static bool Key(KeyCode key) => Input.GetKey(key) && !IsTyping;
    public static bool KeyDown(KeyCode key) => Input.GetKeyDown(key) && !IsTyping;
    public static bool Clicked => Input.GetMouseButtonDown(0) && !PointerOverUI;

    public static bool PointerOverUI => EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

    private static bool IsTyping
    {
        get
        {
            GameObject selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            return selected != null && selected.TryGetComponent(out TMP_InputField field) && field.isFocused;
        }
    }
}
