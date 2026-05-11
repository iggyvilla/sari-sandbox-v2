using UnityEngine;
using UnityEngine.Events;

public class SimpleVRButton : MonoBehaviour
{
    public UnityEvent OnClick;

    public void Tapped()
    {
        OnClick?.Invoke();
    }
}
