using UnityEngine;
using UnityEngine.SceneManagement;

public partial class ShelfBuilder
{
    private const string StoreBuilderSceneName = "StoreBuilder";
    private const float HingeDoorDepth = 0.02f;
    // Gap between the shelf front and the door glass.
    private const float HingeDoorForwardGap = 0.03f;
    private const float HingeDoorBorderWidth = 0.05f;
    // Gap between the door glass and the roof/boot decor strips.
    private const float FridgeDecorForwardGap = 0.01f;

    private float FridgeDoorHeight => shelfLevels * distanceBetweenLevels;
    private float FridgeFrontWidth => shelfWidth + 2f * subShelfHeight;

    void SpawnHingeDoors()
    {
        if (!isFridge) return;

        float doorHeight = FridgeDoorHeight;
        float fullDoorWidth = FridgeFrontWidth;
        bool isDoubleDoor = fridgeDoorStyle == FridgeDoorStyle.Double;

        SpawnHingeDoor(
            isDoubleDoor ? fullDoorWidth / 2 : fullDoorWidth,
            doorHeight,
            isDoubleDoor ? fullDoorWidth / 4 : 0,
            DoorDirection.Left
        );

        if (isDoubleDoor)
            SpawnHingeDoor(fullDoorWidth / 2, doorHeight, -fullDoorWidth / 4, DoorDirection.Right);
    }

    void SpawnHingeDoor(float width, float height, float rightOffset, DoorDirection direction)
    {
        Vector3 position = new Vector3(
            transform.position.x,
            height / 2 + shelfBootHeight,
            transform.position.z
        )
            + transform.forward * (subShelfDepth + HingeDoorForwardGap)
            + transform.right * rightOffset;

        GameObject door = Instantiate(hingeDoorPrefab, position, transform.rotation, transform);
        RemovePhysicsIfInStoreBuilder(door);

        HingedDoorBuilder doorBuilder = door.GetComponentInChildren<HingedDoorBuilder>();
        Vector3 dimensions = new Vector3(width, height, HingeDoorDepth);
        doorBuilder.BuildHingeDoor(
            dimensions,
            HingeDoorBorderWidth,
            direction,
            subShelfDepth,
            FridgeBorderThicknessPadding,
            borderCube
        );
    }

    // Spawns the lit panel, the border strip beneath it, and the badge above the front
    // of the fridge roof. Only runs for fridge shelves.
    void SpawnFridgeRoofDecor()
    {
        if (!isFridge) return;

        float thickness = FridgeBorderThicknessPadding * 2;
        float decorWidth = FridgeFrontWidth;
        float forwardOffset = subShelfDepth + FridgeBorderThicknessPadding + FridgeDecorForwardGap;
        float doorHeight = FridgeDoorHeight;
        float roofTopY = shelfBootHeight + doorHeight + shelfRoofHeight;
        float lightsHeight = shelfRoofHeight * DoorLightPercent;
        float lightsCenterY = roofTopY - lightsHeight / 2f;
        float lightsBottomY = roofTopY - lightsHeight;

        if (fridgeDoorLights != null)
            SpawnFrontStrip(fridgeDoorLights, "FridgeDoorLights", lightsCenterY, lightsHeight, decorWidth, thickness, forwardOffset);

        // The badge sits on the roof border's centre line, or under the lights without a border.
        float badgeAnchorY = lightsBottomY;
        if (borderCube != null)
        {
            float borderHeight = shelfRoofHeight * (1 - DoorLightPercent) - FrontDecorPadding * 2;
            float borderCenterY = lightsBottomY - borderHeight / 2f - FrontDecorPadding;
            badgeAnchorY = borderCenterY;

            SpawnFrontStrip(borderCube, "FridgeRoofBorder", borderCenterY, borderHeight, decorWidth, thickness, forwardOffset);
        }

        if (fridgeBadge != null)
        {
            float badgeX = decorWidth / 2f - FridgeBadgeRightPadding;
            GameObject badge = Instantiate(fridgeBadge, transform);
            badge.name = "FridgeBadge";
            badge.transform.rotation = transform.rotation;
            badge.transform.position =
                new Vector3(transform.position.x, badgeAnchorY, transform.position.z)
                + transform.forward * (forwardOffset + thickness / 2f)
                - transform.right * badgeX;
        }

        if (borderCube != null)
        {
            float bootBorderHeight = shelfBootHeight - FrontDecorPadding;
            SpawnFrontStrip(borderCube, "FridgeBootBorder", bootBorderHeight / 2f, bootBorderHeight, decorWidth, thickness, forwardOffset);
        }
    }

    // Instantiates a flat strip across the fridge front, centred at centerY.
    void SpawnFrontStrip(GameObject prefab, string objectName, float centerY, float height, float width, float thickness, float forwardOffset)
    {
        GameObject strip = Instantiate(prefab, transform);
        strip.name = objectName;
        strip.transform.rotation = transform.rotation;
        strip.transform.position =
            new Vector3(transform.position.x, centerY, transform.position.z)
            + transform.forward * forwardOffset;
        strip.transform.localScale = new Vector3(width, height, thickness);
    }

    void RemovePhysicsIfInStoreBuilder(GameObject hingeDoor)
    {
        if (SceneManager.GetActiveScene().name != StoreBuilderSceneName) return;

        Destroy(hingeDoor.GetComponent<HingeJoint>());
        Destroy(hingeDoor.GetComponent<Rigidbody>());
    }
}
