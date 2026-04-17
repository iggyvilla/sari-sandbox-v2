using JetBrains.Annotations;
using UnityEngine;

public class SB_InteractionController : MonoBehaviour
{
    [Header("Camera Rotation")]
    public float rotationSpeed = 90f; // degrees per second

    [Header("Shelf Placement")]
    public GameObject woodShelfPrefab;
    public GameObject fridgePrefab;
    public float builderGridSize = 1f;

    [Header("References")]
    public SB_UIHandler uiHandler;
    public DataHandler dataHandler;
    public Material airMaterial;

    private GameObject shelfPrefab;
    private bool _placementMode = false;
    public bool IsInPlacementMode => _placementMode;
    [CanBeNull] private GameObject _previewShelf = null;
    [CanBeNull] private ShelfSelector _movingSelector = null;
    private Camera _cam;
    private LayerMask _sariFloorLayerMask;
    private LayerMask _sariInteractableLayerMask;

    void Awake()
    {
        _cam = GetComponentInChildren<Camera>();
        if (_cam == null)
            _cam = Camera.main;
        
        _sariFloorLayerMask = LayerMask.GetMask("SariFloor");
        _sariInteractableLayerMask = LayerMask.GetMask("SariInteractable");
    }

    void Update()
    {
        HandleCameraRotation();

        if (_placementMode)
            HandleShelfPlacement();
    }

    // ── Camera rotation ───────────────────────────────────────────────────────

    void HandleCameraRotation()
    {
        float input = 0f;
        if (Input.GetKey(KeyCode.RightArrow)) input =  1f;
        if (Input.GetKey(KeyCode.LeftArrow))  input = -1f;
        if (input == 0f) return;

        // Rotate around parent's position (acts as pivot at world origin)
        transform.RotateAround(transform.parent.position, Vector3.up, input * rotationSpeed * Time.deltaTime);
    }

    // ── Shelf placement ───────────────────────────────────────────────────────

    // Called by the "Spawn Shelf" UI button
    public void OnSpawnShelfPressed()
    {
        shelfPrefab = woodShelfPrefab;
        ShelfBuilder builder = shelfPrefab.GetComponent<ShelfBuilder>();
        builder.floor = dataHandler.floor;
        _placementMode = true;
    }
    
    // Called by the "Spawn Fridge" UI button
    public void OnSpawnFridgePressed()
    {
        shelfPrefab = fridgePrefab;
        ShelfBuilder builder = shelfPrefab.GetComponent<ShelfBuilder>();
        builder.floor = dataHandler.floor;
        _placementMode = true;
    }

    // Called by ShelfSelector when the user presses M on a selected shelf
    public void EnterMoveMode(ShelfSelector selector)
    {
        UndoSpawnedItems();
        
        _movingSelector = selector;
        _previewShelf = selector.assignedShelf.gameObject;
        _placementMode = true;
        uiHandler.DeselectShelf();
    }

    // Called by ShelfSelector when the user presses D on a selected shelf
    public void DuplicateShelf(ShelfBuilder source)
    {
        UndoSpawnedItems();

        _previewShelf = Instantiate(
            source.gameObject, 
            source.transform.position, 
            Quaternion.identity
        );
        
        _movingSelector = null;
        
        // Enter placement mode
        _placementMode = true;
        // Deselect current shelf
        uiHandler.DeselectShelf();
    }

    void UndoSpawnedItems()
    {
        ShelfBuilder.DespawnAllShelfItemsInScene();
        
        // Reverts shelves to not spawn items again
        foreach (ShelfBuilder shelf in FindObjectsByType<ShelfBuilder>(FindObjectsSortMode.None))
        {
            shelf.spawnItems = false;
        }
    }

    void HandleShelfPlacement()
    {
        if (Input.GetKeyDown(KeyCode.R) && _previewShelf != null)
        {
            ShelfBuilder builder = _previewShelf.GetComponent<ShelfBuilder>();
            if (builder != null)
            {
                builder.rotationY = (builder.rotationY + 90f) % 360f;
                builder.Rebuild();
                if (_movingSelector != null)
                    _movingSelector.EncapsulateShelf(_movingSelector.assignedShelf);
            }
        }

        bool hitFloor = RaycastFloor(out Vector3 worldPos);

        if (hitFloor)
        {
            Vector3 snapped = SnapToGrid(worldPos);

            if (_previewShelf == null)
            {
                SpawnPreview(snapped);
            }
            else
            {
                _previewShelf.transform.position = snapped;
                if (_movingSelector != null)
                    _movingSelector.EncapsulateShelf(_movingSelector.assignedShelf);
            }

            if (Input.GetMouseButtonDown(0))
            {
                ConfirmPlacement();
            }
        }
        else
        {
            if (_movingSelector == null)
            {
                // Normal new-shelf placement: destroy the preview
                DestroyPreview();
            }
            // Move mode: keep the shelf at its last valid position until the user clicks

            // Left-click off the floor: treat as a cancellation
            if (Input.GetMouseButtonDown(0))
            {
                if (_movingSelector != null)
                {
                    // Delete the existing shelf and its selector outline box
                    Destroy(_movingSelector.assignedShelf.gameObject);
                    Destroy(_movingSelector.gameObject);
                    _movingSelector = null;
                    _previewShelf = null;
                }
                ExitPlacementMode();
            }
        }
    }

    bool RaycastFloor(out Vector3 hitPoint)
    {
        hitPoint = Vector3.zero;
        Ray ray = _cam.ScreenPointToRay(Input.mousePosition);
        
        if (Physics.Raycast(ray, out RaycastHit hit, 100f, _sariFloorLayerMask))
        {
            if (hit.collider.CompareTag("Floor"))
            {
                hitPoint = hit.point;
                return true;
            }
        }
        return false;
    }

    Vector3 SnapToGrid(Vector3 worldPos)
    {
        if (builderGridSize <= 0f) return worldPos;
        return new Vector3(
            Mathf.Round(worldPos.x / builderGridSize) * builderGridSize,
            worldPos.y,
            Mathf.Round(worldPos.z / builderGridSize) * builderGridSize
        );
    }

    void SpawnPreview(Vector3 position)
    {
        _previewShelf = Instantiate(shelfPrefab, position, Quaternion.identity);
    }

    void ConfirmPlacement()
    {
        if (_movingSelector != null)
        {
            // Finalise the repositioned shelf — re-encapsulate the selector outline box
            _movingSelector.EncapsulateShelf(_movingSelector.assignedShelf);
            _movingSelector.assignedShelf.Rebuild();
            _movingSelector = null;
        }
        else if (_previewShelf != null)
        {
            ShelfBuilder builder = _previewShelf.GetComponent<ShelfBuilder>();
            builder.shelfId = dataHandler.GetUniqueShelfId();
            SummonOutlineBox(builder);
        }

        _previewShelf = null; // relinquish ownership — the shelf stays in the scene
        ExitPlacementMode();
    }

    void SummonOutlineBox(ShelfBuilder shelf)
    {
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.layer = LayerMask.NameToLayer("SariInteractable");
        cube.GetComponent<Renderer>().material = airMaterial;

        // Make collider a trigger so it doesn't interfere with physics
        // but is still hit by raycasts (Physics.queriesHitTriggers = true by default)
        cube.GetComponent<BoxCollider>().isTrigger = true;
        
        // Add OutlineFx for the white outline
        cube.AddComponent<OutlineFx.OutlineFx>();

        // Wire up the selector
        ShelfSelector selector = cube.AddComponent<ShelfSelector>();
        selector.assignedShelf = shelf;
        selector.uiHandler = uiHandler;
        selector.interactionController = this;
        
        // Encapsulate shelf bounds
        selector.EncapsulateShelf(shelf);
    }

    void DestroyPreview()
    {
        if (_previewShelf != null)
        {
            Destroy(_previewShelf);
            _previewShelf = null;
        }
    }

    void ExitPlacementMode()
    {
        _placementMode = false;
    }
}
