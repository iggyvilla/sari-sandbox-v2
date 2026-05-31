using System.Collections.Generic;
using UnityEngine;

public partial class ShelfBuilder
{
    private Material EffectiveShelfMaterial => isFridge ? metalShelfMaterial : shelfMaterial;
    private Material EffectiveWallMaterial => isFridge ? metalShelfMaterial : wallMaterial;

    void BuildRectangularShelf()
    {
        float wallThickness = subShelfHeight;
        float groundY = floor.transform.position.y;

        float shelvesZOffset = (subShelfDepth + wallThickness) / 2;
        BuildSubShelf(transform, MyZWithOffset(shelvesZOffset), 0, groundY, 0, shelfWidth, frontShelfConfig);
        BuildSubShelf(transform, MyZWithOffset(-shelvesZOffset), 1, groundY, 180, shelfWidth, backShelfConfig);

        float shelvesXOffset = subShelfDepth / 2 + shelfWidth / 2 + wallThickness;
        sideShelfWidth = CalculateShelfWidth(wallThickness, frontShelfConfig, backShelfConfig);

        BuildSubShelf(
            transform,
            MyPosWithOffset(shelvesXOffset, sideShelfWidth, frontShelfConfig, backShelfConfig),
            2,
            groundY,
            90,
            sideShelfWidth,
            leftShelfConfig
        );
        BuildSubShelf(
            transform,
            MyPosWithOffset(-shelvesXOffset, sideShelfWidth, frontShelfConfig, backShelfConfig),
            3,
            groundY,
            270,
            sideShelfWidth,
            rightShelfConfig
        );

        // Items are spawned later, after all rotations and translations are complete.
        transform.Rotate(Vector3.up, rotationY);

        SpawnHingeDoors();
        SpawnFridgeRoofDecor();
    }

    float CalculateShelfWidth(float wallThickness, ShelfConfiguration frontShelfCfg, ShelfConfiguration backShelfCfg)
    {
        int divisor = frontShelfCfg.buildShelves ^ backShelfCfg.buildShelves ? 2 : 1;
        return (subShelfDepth * 2 + wallThickness) / divisor;
    }

    public float CalculateShelfHeight()
    {
        return distanceBetweenLevels * shelfLevels + shelfBootHeight + shelfRoofHeight;
    }

    /*
     *  2D CROSS-SECTION (NOT TOP VIEW):
     *          |----------------------------|
     *     ^    |                            |
     *     |    |----------------------------|
     *  Height           Depth -->
     *
     *  Width is how much it extrudes.
     */
    void BuildSubShelf(Transform parent, Vector3 spawnPos, int subShelfId, float floorY, float rotY, float width, ShelfConfiguration shelfConfig)
    {
        GameObject emptyParent = new GameObject("ShelfGroup" + shelfId);
        emptyParent.transform.position = spawnPos;
        emptyParent.transform.SetParent(parent);

        if (shelfConfig.buildBackWall) BuildShelfWall(subShelfHeight, width, subShelfDepth, emptyParent.transform);
        if (!shelfConfig.buildShelves)
        {
            emptyParent.transform.Rotate(Vector3.up, rotY);
            return;
        }

        // The lowest shelf keeps the side profile at its natural y; a separate boot cube fills the
        // space beneath it so that boot + profile together total shelfBootHeight.
        Vector3 shelfPosition = new Vector3(spawnPos.x, floorY + shelfBootHeight - subShelfHeight / 2, spawnPos.z);
        List<float> shelfBottomYs = new List<float>();

        for (int i = 0; i < shelfLevels + 1; i++)
        {
            if (!shelfConfig.buildShelfRoof && i == shelfLevels) continue;

            bool isBottomShelf = i == 0;
            bool roof = i == shelfLevels && shelfConfig.buildShelfRoof;
            if (roof) shelfPosition.y += shelfRoofHeight / 2;

            GameObject shelfExtruded = Instantiate(
                shelfSideProfile,
                shelfPosition,
                shelfSideProfile.transform.rotation,
                parent
            );

            shelfExtruded.layer = LayerMask.NameToLayer(roof ? "SariInteractable" : "SariShelf");
            shelfExtruded.tag = "Wall";
            shelfExtruded.name = "Shelf" + i;
            shelfExtruded.GetComponent<Renderer>().sharedMaterial = EffectiveShelfMaterial;

            Vector3 extrudedScale = shelfSideProfile.transform.localScale;
            extrudedScale.x = width;
            if (roof)
                extrudedScale.y = shelfRoofHeight;

            shelfExtruded.transform.localScale = extrudedScale;
            if (!roof) shelfBottomYs.Add(shelfPosition.y - extrudedScale.y / 2f);

            if (!roof)
            {
                var outline = shelfExtruded.AddComponent<OutlineFx.OutlineFx>();
                outline.enabled = false;

                SubShelfMarker marker = shelfExtruded.AddComponent<SubShelfMarker>();
                marker.shelfInfo = new ShelfInfo { shelfId = shelfId, subShelfId = subShelfId, subSubShelfId = i };
                marker.parentShelf = this;
            }

            if (spawnItems && !roof) InitializeItemSpawner(shelfExtruded, subShelfId, i);

            if (isBottomShelf && !roof)
                BuildShelfBoot(emptyParent.transform, shelfPosition.x, shelfPosition.z, floorY, width);

            shelfExtruded.isStatic = true;
            shelfPosition.y += distanceBetweenLevels + (isBottomShelf ? subShelfHeight / 2 : roof ? shelfRoofHeight / 2 : 0);
            shelfExtruded.transform.SetParent(emptyParent.transform);
        }

        BuildRailsAndSupports(emptyParent.transform, spawnPos, width, shelfBottomYs);
        emptyParent.transform.Rotate(Vector3.up, rotY);
    }

    // The boot is the solid base cube beneath the lowest shelf profile. Its height fills whatever
    // shelfBootHeight leaves above the profile, so the two together span exactly shelfBootHeight.
    void BuildShelfBoot(Transform parent, float x, float z, float floorY, float width)
    {
        float bootHeight = shelfBootHeight - subShelfHeight;
        if (bootHeight <= 0f) return;

        GameObject boot = GameObject.CreatePrimitive(PrimitiveType.Cube);
        boot.layer = LayerMask.NameToLayer("SariShelf");
        boot.tag = "Wall";
        boot.name = "ShelfBoot";
        boot.transform.localScale = new Vector3(width, bootHeight, subShelfDepth);
        boot.transform.position = new Vector3(x, floorY + bootHeight / 2f, z);
        boot.GetComponent<Renderer>().sharedMaterial = shelfBootMaterial;
        boot.isStatic = true;
        boot.transform.SetParent(parent);
    }

    void BuildRailsAndSupports(Transform parent, Vector3 spawnPos, float width, List<float> shelfBottomYs)
    {
        if (shelfRail == null && shelfSupport == null) return;

        float wallHeight = CalculateShelfHeight();
        float backWallFrontZ = spawnPos.z - subShelfDepth / 2f;
        float railDepth = shelfRail != null ? shelfRail.transform.localScale.z : 0f;
        float railZ = backWallFrontZ + railDepth / 2f;

        List<float> railXOffsets = new List<float>
        {
            -(width / 2f - ShelfRailPadding),
            width / 2f - ShelfRailPadding
        };
        if (width > ShelfRailThreshold) railXOffsets.Add(0f);

        foreach (float railXOffset in railXOffsets)
        {
            float x = spawnPos.x + railXOffset;
            if (shelfRail != null) BuildRail(parent, x, railZ, wallHeight);
            if (shelfSupport != null) BuildSupports(parent, x, railZ, shelfBottomYs);
        }
    }

    void BuildRail(Transform parent, float x, float z, float wallHeight)
    {
        GameObject rail = Instantiate(
            shelfRail,
            new Vector3(x, wallHeight / 2f, z),
            shelfRail.transform.rotation,
            parent
        );
        rail.name = "ShelfRail";

        Vector3 railScale = shelfRail.transform.localScale;
        // The imported rail's "up" axis is X.
        railScale.x = wallHeight;
        rail.transform.localScale = railScale;

        Renderer railRenderer = rail.GetComponentInChildren<Renderer>();
        if (railRenderer != null)
        {
            Material[] railMaterials = railRenderer.materials;
            if (railMaterials.Length > 1)
            {
                Material railMaterial = railMaterials[1];
                Vector2 tiling = railMaterial.mainTextureScale;
                tiling.y = 80 * railScale.x;
                railMaterial.mainTextureScale = tiling;
            }
        }

        rail.isStatic = true;
    }

    void BuildSupports(Transform parent, float x, float z, List<float> shelfBottomYs)
    {
        foreach (float shelfBottomY in shelfBottomYs)
        {
            GameObject support = Instantiate(
                shelfSupport,
                new Vector3(x, shelfBottomY, z),
                shelfSupport.transform.rotation,
                parent
            );
            support.name = "ShelfSupport";
            support.isStatic = true;
        }
    }

    // wallOffset is how far from the edge of a shelf the wall will spawn at.
    void BuildShelfWall(float wallThickness, float wallWidth, float wallOffset, Transform parent)
    {
        float wallHeight = CalculateShelfHeight();
        GameObject backWall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        backWall.layer = LayerMask.NameToLayer("SariShelf");
        backWall.tag = "Wall";
        backWall.name = "BackWall";
        
        backWall.transform.localScale = new Vector3(wallWidth, wallHeight, wallThickness);

        Vector3 backWallPos = parent.position;
        backWallPos.z = parent.position.z - (wallOffset + wallThickness) / 2;
        backWallPos.y = wallHeight / 2;
        backWall.transform.position = backWallPos;
            
        backWall.isStatic = true;

        Renderer r = backWall.GetComponent<Renderer>();

        r.sharedMaterial = EffectiveWallMaterial;
        
        backWall.transform.SetParent(parent);
    }

    Vector3 MyPosWithOffset(float offset, float width, ShelfConfiguration frontShelfCfg, ShelfConfiguration backShelfCfg)
    {
        float zOffset = 0;
        if (frontShelfCfg.buildShelves ^ backShelfCfg.buildShelves)
            zOffset = frontShelfCfg.buildShelves ? width / 2 : -width / 2;

        return new Vector3(transform.position.x + offset, transform.position.y, transform.position.z + zOffset);
    }

    Vector3 MyZWithOffset(float offset)
    {
        return new Vector3(transform.position.x, transform.position.y, transform.position.z + offset);
    }
}
