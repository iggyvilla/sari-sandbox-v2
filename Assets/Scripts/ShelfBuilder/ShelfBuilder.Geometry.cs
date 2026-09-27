using System.Collections.Generic;
using UnityEngine;

public partial class ShelfBuilder
{
    private Material EffectiveShelfMaterial => isFridge ? metalShelfMaterial : shelfMaterial;
    private Material EffectiveWallMaterial => isFridge ? metalShelfMaterial : wallMaterial;

    // Rail texture repeats per metre of rail height.
    private const float RailTilesPerMeter = 80f;
    // Shared tiled rail materials keyed by (source material, tiling) so rebuilds don't leak copies.
    private static readonly Dictionary<(Material, float), Material> RailMaterialCache = new();

    void BuildRectangularShelf()
    {
        float wallThickness = subShelfHeight;
        float groundY = floor.transform.position.y;

        float shelvesZOffset = (subShelfDepth + wallThickness) / 2;
        BuildSubShelf(transform, MyZWithOffset(shelvesZOffset), 0, groundY, 0, shelfWidth, frontShelfConfig);
        BuildSubShelf(transform, MyZWithOffset(-shelvesZOffset), 1, groundY, 180, shelfWidth, backShelfConfig);

        float shelvesXOffset = subShelfDepth / 2 + shelfWidth / 2 + wallThickness;
        float sideShelfWidth = CalculateShelfWidth(wallThickness, frontShelfConfig, backShelfConfig);

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

            MarkStaticWall(shelfExtruded, "Shelf" + i, roof ? "SariInteractable" : "SariShelf");
            shelfExtruded.GetComponent<Renderer>().sharedMaterial = EffectiveShelfMaterial;

            Vector3 extrudedScale = shelfSideProfile.transform.localScale;
            extrudedScale.x = width;
            if (roof)
                extrudedScale.y = shelfRoofHeight;

            shelfExtruded.transform.localScale = extrudedScale;

            if (!roof)
            {
                shelfBottomYs.Add(shelfPosition.y - extrudedScale.y / 2f);

                var outline = shelfExtruded.AddComponent<OutlineFx.OutlineFx>();
                outline.enabled = false;

                SubShelfMarker marker = shelfExtruded.AddComponent<SubShelfMarker>();
                marker.shelfInfo = new ShelfInfo { shelfId = shelfId, subShelfId = subShelfId, subSubShelfId = i };
                marker.parentShelf = this;
            }

            if (spawnItems && !roof) InitializeItemSpawner(shelfExtruded, subShelfId, i);

            if (isBottomShelf && !roof)
                BuildShelfBoot(emptyParent.transform, shelfPosition.x, shelfPosition.z, floorY, width);

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
        MarkStaticWall(boot, "ShelfBoot");
        boot.transform.localScale = new Vector3(width, bootHeight, subShelfDepth);
        boot.transform.position = new Vector3(x, floorY + bootHeight / 2f, z);
        boot.GetComponent<Renderer>().sharedMaterial = shelfBootMaterial;
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
            Material[] railMaterials = railRenderer.sharedMaterials;
            if (railMaterials.Length > 1 && railMaterials[1] != null)
            {
                railMaterials[1] = GetTiledRailMaterial(railMaterials[1], RailTilesPerMeter * railScale.x);
                railRenderer.sharedMaterials = railMaterials;
            }
        }

        rail.isStatic = true;
    }

    static Material GetTiledRailMaterial(Material source, float tilingY)
    {
        var key = (source, tilingY);
        if (RailMaterialCache.TryGetValue(key, out Material tiled) && tiled != null)
            return tiled;

        tiled = new Material(source);
        Vector2 tiling = tiled.mainTextureScale;
        tiling.y = tilingY;
        tiled.mainTextureScale = tiling;
        RailMaterialCache[key] = tiled;
        return tiled;
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
        MarkStaticWall(backWall, "BackWall");

        backWall.transform.localScale = new Vector3(wallWidth, wallHeight, wallThickness);

        Vector3 backWallPos = parent.position;
        backWallPos.z = parent.position.z - (wallOffset + wallThickness) / 2;
        backWallPos.y = wallHeight / 2;
        backWall.transform.position = backWallPos;

        Renderer r = backWall.GetComponent<Renderer>();

        r.sharedMaterial = EffectiveWallMaterial;
        
        backWall.transform.SetParent(parent);
    }

    // Shared setup for static shelf geometry.
    static void MarkStaticWall(GameObject go, string objectName, string layerName = "SariShelf")
    {
        go.name = objectName;
        go.layer = LayerMask.NameToLayer(layerName);
        go.tag = "Wall";
        go.isStatic = true;
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
