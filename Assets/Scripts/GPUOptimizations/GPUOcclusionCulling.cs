using System;
using UnityEngine;

// Conservative intentionally remains zero so existing serialized trackers adopt it by default.
public enum OcclusionCullingMode
{
    Conservative = 0,
    Balanced = 1,
    Aggressive = 2,
    Disabled = 3
}

public readonly struct OcclusionQualitySettings
{
    public readonly int samplePattern;
    public readonly float depthBiasMeters;
    public readonly int occludedUpdatesBeforeRemoval;
    public readonly bool buildsDepthPyramid;

    public OcclusionQualitySettings(
        int samplePattern,
        float depthBiasMeters,
        int occludedUpdatesBeforeRemoval,
        bool buildsDepthPyramid)
    {
        this.samplePattern = samplePattern;
        this.depthBiasMeters = depthBiasMeters;
        this.occludedUpdatesBeforeRemoval = occludedUpdatesBeforeRemoval;
        this.buildsDepthPyramid = buildsDepthPyramid;
    }
}

public static class GPUOcclusionCulling
{
    // Kept on a dedicated camera-visible layer so auxiliary scene-only passes can
    // exclude indirect products without changing their normal rendering.
    public const int ProductLayer = 31;

    public static OcclusionQualitySettings GetQualitySettings(OcclusionCullingMode mode)
    {
        switch (mode)
        {
            case OcclusionCullingMode.Conservative:
                return new OcclusionQualitySettings(0, 0.10f, 2, true);
            case OcclusionCullingMode.Balanced:
                return new OcclusionQualitySettings(1, 0.05f, 1, true);
            case OcclusionCullingMode.Aggressive:
                return new OcclusionQualitySettings(2, 0.02f, 1, false);
            default:
                return new OcclusionQualitySettings(0, 0f, 1, false);
        }
    }

    public static Bounds TransformBounds(Bounds localBounds, LodTransform transformData)
    {
        Quaternion rotation = new Quaternion(
            transformData.rotation.x,
            transformData.rotation.y,
            transformData.rotation.z,
            transformData.rotation.w);
        float rotationMagnitudeSquared =
            rotation.x * rotation.x +
            rotation.y * rotation.y +
            rotation.z * rotation.z +
            rotation.w * rotation.w;
        if (rotationMagnitudeSquared < 0.000001f)
            rotation = Quaternion.identity;
        else
        {
            float inverseMagnitude = 1f / Mathf.Sqrt(rotationMagnitudeSquared);
            rotation = new Quaternion(
                rotation.x * inverseMagnitude,
                rotation.y * inverseMagnitude,
                rotation.z * inverseMagnitude,
                rotation.w * inverseMagnitude);
        }

        Vector3 worldCenter =
            transformData.position +
            rotation * Vector3.Scale(localBounds.center, transformData.scale);
        Vector3 scaledExtents = Vector3.Scale(localBounds.extents, transformData.scale);

        Vector3 axisX = rotation * new Vector3(scaledExtents.x, 0f, 0f);
        Vector3 axisY = rotation * new Vector3(0f, scaledExtents.y, 0f);
        Vector3 axisZ = rotation * new Vector3(0f, 0f, scaledExtents.z);
        Vector3 worldExtents = new Vector3(
            Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
            Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
            Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));

        return new Bounds(worldCenter, worldExtents * 2f);
    }

    public static Bounds UnionInstanceBounds(InstanceData instance, LODDefinition[] lods)
    {
        if (lods == null || lods.Length == 0)
            return new Bounds(instance.lod0.position, Vector3.zero);

        bool initialized = false;
        Bounds union = default;
        for (int lodIndex = 0; lodIndex < Mathf.Min(lods.Length, 4); lodIndex++)
        {
            Mesh mesh = lods[lodIndex].mesh;
            if (mesh == null)
                continue;

            Bounds transformed = TransformBounds(
                mesh.bounds,
                GetLodTransform(instance, lodIndex));
            if (!initialized)
            {
                union = transformed;
                initialized = true;
            }
            else
            {
                union.Encapsulate(transformed.min);
                union.Encapsulate(transformed.max);
            }
        }

        return initialized
            ? union
            : new Bounds(instance.lod0.position, Vector3.zero);
    }

    private static LodTransform GetLodTransform(InstanceData instance, int lod)
    {
        switch (lod)
        {
            case 0: return instance.lod0;
            case 1: return instance.lod1;
            case 2: return instance.lod2;
            case 3: return instance.lod3;
            default: throw new ArgumentOutOfRangeException(nameof(lod));
        }
    }
}
