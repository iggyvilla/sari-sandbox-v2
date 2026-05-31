using UnityEngine;
using UnityEngine.SceneManagement;

public partial class ShelfBuilder
{
    void SpawnHingeDoors()
    {
        if (!isFridge) return;

        float doorHeight = shelfLevels * distanceBetweenLevels;
        float fullDoorWidth = shelfWidth + 2f * subShelfHeight;
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
        const float doorDepth = 0.02f;

        Vector3 position = new Vector3(
            transform.position.x,
            height / 2 + shelfBootHeight,
            transform.position.z
        )
            + transform.forward * (subShelfDepth + 0.03f)
            + transform.right * rightOffset;

        GameObject door = Instantiate(hingeDoorPrefab, position, transform.rotation, transform);
        RemovePhysicsIfInStoreBuilder(door);

        HingedDoorBuilder doorBuilder = door.GetComponentInChildren<HingedDoorBuilder>();
        Vector3 dimensions = new Vector3(width, height, doorDepth);
        doorBuilder.BuildHingeDoor(
            dimensions,
            0.05f,
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
        float decorWidth = shelfWidth + 2f * subShelfHeight;
        float forwardOffset = subShelfDepth + FridgeBorderThicknessPadding + 0.01f;
        float doorHeight = shelfLevels * distanceBetweenLevels;
        float roofTopY = shelfBootHeight + doorHeight + shelfRoofHeight;
        float lightsHeight = shelfRoofHeight * DoorLightPercent;
        float lightsCenterY = roofTopY - lightsHeight / 2f;
        float lightsBottomY = roofTopY - lightsHeight;

        if (fridgeDoorLights != null)
        {
            GameObject lights = Instantiate(fridgeDoorLights, transform);
            lights.name = "FridgeDoorLights";
            lights.transform.rotation = transform.rotation;
            lights.transform.position =
                new Vector3(transform.position.x, lightsCenterY, transform.position.z)
                + transform.forward * forwardOffset;
            lights.transform.localScale = new Vector3(decorWidth, lightsHeight, thickness);
        }

        float borderBottomRefY = lightsBottomY;
        if (borderCube != null)
        {
            GameObject border = Instantiate(borderCube, transform);
            border.name = "FridgeRoofBorder";

            float borderHeight = shelfRoofHeight * (1 - DoorLightPercent) - FrontDecorPadding * 2;
            float borderCenterY = lightsBottomY - borderHeight / 2f - FrontDecorPadding;
            borderBottomRefY = borderCenterY;

            border.transform.rotation = transform.rotation;
            border.transform.position =
                new Vector3(transform.position.x, borderCenterY, transform.position.z)
                + transform.forward * forwardOffset;
            border.transform.localScale = new Vector3(decorWidth, borderHeight, thickness);
        }

        if (fridgeBadge != null)
        {
            float badgeX = decorWidth / 2f - FridgeBadgeRightPadding;
            GameObject badge = Instantiate(fridgeBadge, transform);
            badge.name = "FridgeBadge";
            badge.transform.rotation = transform.rotation;
            badge.transform.position =
                new Vector3(transform.position.x, borderBottomRefY, transform.position.z)
                + transform.forward * (forwardOffset + thickness / 2f)
                - transform.right * badgeX;
        }

        if (borderCube != null)
        {
            GameObject bootBorder = Instantiate(borderCube, transform);
            bootBorder.name = "FridgeBootBorder";

            float bootBorderHeight = shelfBootHeight - FrontDecorPadding;
            float bootBorderCenterY = bootBorderHeight / 2f;

            bootBorder.transform.rotation = transform.rotation;
            bootBorder.transform.position =
                new Vector3(transform.position.x, bootBorderCenterY, transform.position.z)
                + transform.forward * forwardOffset;
            bootBorder.transform.localScale = new Vector3(decorWidth, bootBorderHeight, thickness);
        }
    }

    void RemovePhysicsIfInStoreBuilder(GameObject hingeDoor)
    {
        if (SceneManager.GetActiveScene().name != "StoreBuilder") return;

        Destroy(hingeDoor.GetComponent<HingeJoint>());
        Destroy(hingeDoor.GetComponent<Rigidbody>());
    }
}
