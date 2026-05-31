using System;
using System.Numerics;
using Unity.Mathematics.Geometry;
using UnityEngine;
using Quaternion = UnityEngine.Quaternion;
using Vector3 = UnityEngine.Vector3;

public enum DoorDirection
{
    Left,
    Right
}

public class HingedDoorBuilder : MonoBehaviour
{
    private HingeJoint _hingeJoint;
    [SerializeField] private GameObject glassDoor;
    [SerializeField] private GameObject doorHandle;
    [SerializeField] private BoxCollider doorTrigger;
    // Cube with a special texture for border
    [SerializeField] private GameObject borderCube;
    private bool _doorStatus;

    // How wide (along the door face) each border strip is.
    private const float FridgeBorderWidth = 0.05f;
    // Extra thickness the border sticks out past the glass on the front and back.
    private const float FridgeBorderThicknessPadding = 0.02f;
    // Glass is scaled down slightly so its edges don't sit exactly on the border
    // faces, which would otherwise z-fight.
    private const float FridgeGlassShrink = 0.99f;

    private Vector3 _closedTriggerSize;
    private Vector3 _closedTriggerCenter;

    [SerializeField] private float startAngle;

    void Awake()
    {
        _hingeJoint = GetComponentInChildren<HingeJoint>();
        startAngle = transform.rotation.eulerAngles.y;
    }

    // NOTE: handlePadding is deprecated and no longer used. The handle now spawns on the
    // left/right fridge border (opposite the hinge). Kept in the signature for callers.
    public void BuildHingeDoor(Vector3 doorDimensions, float handlePadding, DoorDirection direction, float subShelfDepth)
    {
        // Shrink the glass a hair so its rim doesn't z-fight with the border faces.
        glassDoor.transform.localScale = doorDimensions * FridgeGlassShrink;

        // The handle sits on the border edge opposite the hinge.
        float handleSide;

        if (direction == DoorDirection.Left)
        {
            _hingeJoint.anchor = new Vector3(doorDimensions.x/2, 0, 0);
            handleSide = -1f;
        }
        else
        {
            _hingeJoint.anchor = new Vector3(-doorDimensions.x/2, 0, 0);
            handleSide = 1f;
            JointLimits limits = new JointLimits
            {
                min = 0,
                max = -90
            };
            _hingeJoint.limits = limits;
        }

        // Centre the handle on the border strip (x), and push it forward so it rests on
        // the border's front face rather than being buried inside the thicker border (z).
        float handleX = handleSide * (doorDimensions.x / 2f - FridgeBorderWidth / 2f);
        float borderFrontZ = (doorDimensions.z + FridgeBorderThicknessPadding) / 2f;
        doorHandle.transform.position += transform.right * handleX + transform.forward * borderFrontZ;

        // doorTrigger is parented to glassDoor whose localScale == doorDimensions,
        // so divide world-space extents by that scale to get local collider values.
        Vector3 scale = glassDoor.transform.localScale;
        float colliderDepth = subShelfDepth + doorDimensions.z;
        _closedTriggerSize = new Vector3(1f, 1f, colliderDepth / scale.z);
        _closedTriggerCenter = new Vector3(0, 0, (doorDimensions.z - subShelfDepth) / 2f / scale.z);
        doorTrigger.size = _closedTriggerSize;
        doorTrigger.center = _closedTriggerCenter;

        BuildFridgeBorder(doorDimensions);
    }

    // Frames the glass door with four cube strips along its perimeter.
    private void BuildFridgeBorder(Vector3 doorDimensions)
    {
        float thickness = doorDimensions.z + FridgeBorderThicknessPadding;
        float halfW = doorDimensions.x / 2f;
        float halfH = doorDimensions.y / 2f;

        // Top and bottom span the full width; left and right fill the gap between them.
        float sideHeight = doorDimensions.y - 2f * FridgeBorderWidth;

        // Top
        CreateBorderCube(
            new Vector3(doorDimensions.x, FridgeBorderWidth, thickness),
            new Vector3(0f, halfH - FridgeBorderWidth / 2f, 0f));
        // Bottom
        CreateBorderCube(
            new Vector3(doorDimensions.x, FridgeBorderWidth, thickness),
            new Vector3(0f, -halfH + FridgeBorderWidth / 2f, 0f));
        // Left
        CreateBorderCube(
            new Vector3(FridgeBorderWidth, sideHeight, thickness),
            new Vector3(-halfW + FridgeBorderWidth / 2f, 0f, 0f));
        // Right
        CreateBorderCube(
            new Vector3(FridgeBorderWidth, sideHeight, thickness),
            new Vector3(halfW - FridgeBorderWidth / 2f, 0f, 0f));
    }

    // Spawns a single border strip. worldSize/worldOffset are expressed in the glass
    // door's world dimensions; the cube is parented to glassDoor and counter-scaled so
    // the parent's localScale (== doorDimensions) doesn't distort it.
    private void CreateBorderCube(Vector3 worldSize, Vector3 worldOffset)
    {
        // GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        GameObject cube = Instantiate(borderCube, glassDoor.transform);
        cube.name = "FridgeBorder";

        // Border is purely visual; drop the collider so it can't snag the door trigger.
        Destroy(cube.GetComponent<BoxCollider>());

        Vector3 parentScale = glassDoor.transform.localScale;
        // cube.transform.SetParent(glassDoor.transform, false);
        cube.transform.localScale = new Vector3(
            worldSize.x / parentScale.x,
            worldSize.y / parentScale.y,
            worldSize.z / parentScale.z);
        cube.transform.localPosition = new Vector3(
            worldOffset.x / parentScale.x,
            worldOffset.y / parentScale.y,
            worldOffset.z / parentScale.z);
        cube.transform.localRotation = Quaternion.identity;
    }

    public bool IsDoorClosed()
    {
        float yDeg = transform.rotation.eulerAngles.y;
        return yDeg <= startAngle + 5 && yDeg >= startAngle - 5;
    }

    void Update()
    {
        if (IsDoorClosed())
        {
            doorTrigger.size = _closedTriggerSize;
            doorTrigger.center = _closedTriggerCenter;
        }
        else
        {
            doorTrigger.size = Vector3.one;
            doorTrigger.center = Vector3.zero;
        }
    }

    public void ApplyHandForce(Vector3 worldForce)
    {
        Rigidbody rb = GetComponent<Rigidbody>();
        rb.AddForce(worldForce);
    }

    public void ToggleDoor()
    {
        Rigidbody rb = GetComponent<Rigidbody>();

        float closeForce = 15.0f;
        
        if (IsDoorClosed())
            rb.AddForce(transform.forward * closeForce);
        else
            rb.AddForce(-transform.forward * closeForce);
    }
    
}
