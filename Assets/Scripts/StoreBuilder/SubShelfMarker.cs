using UnityEngine;

public class SubShelfMarker : MonoBehaviour
{
    public ShelfInfo shelfInfo;
    public ShelfBuilder parentShelf;

    private OutlineFx.OutlineFx _outlineFx;

    void Awake()
    {
        _outlineFx = GetComponent<OutlineFx.OutlineFx>();
    }

    public void EnableOutline(bool on)
    {
        if (_outlineFx != null)
            _outlineFx.enabled = on;
    }
}
