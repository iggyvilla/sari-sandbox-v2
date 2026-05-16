using UnityEngine;

public class TitleScreenScroller : MonoBehaviour
{
    [Header("Spawning")]
    [SerializeField] private GameObject prefab;
    [SerializeField] private int count = 5;
    [SerializeField] private float spacing = 3f;

    [Header("Movement")]
    [SerializeField] private float speed = 2f;
    [SerializeField] private float recycleZ = -10f;   // z at which an object wraps to the back

    private Transform[] _objects;
    private float _totalLength;

    void Start()
    {
        _totalLength = count * spacing;
        _objects = new Transform[count];

        for (int i = 0; i < count; i++)
        {
            Vector3 pos = new Vector3(transform.position.x, transform.position.y, transform.position.z + i * spacing);
            _objects[i] = Instantiate(prefab, pos, Quaternion.identity, transform).transform;
        }
    }

    void Update()
    {
        float delta = speed * Time.deltaTime;

        for (int i = 0; i < _objects.Length; i++)
        {
            _objects[i].position -= Vector3.forward * delta;

            if (_objects[i].position.z <= recycleZ)
            {
                float furthestZ = GetFurthestZ();
                _objects[i].position = new Vector3(_objects[i].position.x, _objects[i].position.y, furthestZ + spacing);
            }
        }
    }

    float GetFurthestZ()
    {
        float max = float.MinValue;
        foreach (Transform t in _objects)
            if (t.position.z > max) max = t.position.z;
        return max;
    }
}
