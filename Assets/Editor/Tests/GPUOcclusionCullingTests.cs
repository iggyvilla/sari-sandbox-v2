using NUnit.Framework;
using UnityEngine;

public sealed class GPUOcclusionCullingTests
{
    [Test]
    public void TransformBounds_AppliesRotationScaleAndTranslation()
    {
        Bounds local = new Bounds(
            new Vector3(1f, -2f, 0.5f),
            new Vector3(2f, 4f, 6f));
        Quaternion rotation = Quaternion.Euler(0f, 90f, 0f);
        LodTransform transformData = new LodTransform
        {
            position = new Vector3(10f, 20f, 30f),
            rotation = new Vector4(rotation.x, rotation.y, rotation.z, rotation.w),
            scale = new Vector3(2f, 0.5f, -3f)
        };

        Bounds result = GPUOcclusionCulling.TransformBounds(local, transformData);

        Assert.That(result.center.x, Is.EqualTo(8.5f).Within(0.0001f));
        Assert.That(result.center.y, Is.EqualTo(19f).Within(0.0001f));
        Assert.That(result.center.z, Is.EqualTo(28f).Within(0.0001f));
        Assert.That(result.size.x, Is.EqualTo(18f).Within(0.0001f));
        Assert.That(result.size.y, Is.EqualTo(2f).Within(0.0001f));
        Assert.That(result.size.z, Is.EqualTo(4f).Within(0.0001f));
    }

    [Test]
    public void UnionInstanceBounds_ContainsEveryActiveLod()
    {
        Mesh first = new Mesh();
        Mesh second = new Mesh();
        try
        {
            first.bounds = new Bounds(Vector3.zero, new Vector3(2f, 2f, 2f));
            second.bounds = new Bounds(Vector3.zero, new Vector3(4f, 2f, 2f));
            InstanceData instance = new InstanceData
            {
                lod0 = TransformAt(new Vector3(-3f, 0f, 0f), Quaternion.identity, Vector3.one),
                lod1 = TransformAt(
                    new Vector3(4f, 0f, 0f),
                    Quaternion.Euler(0f, 0f, 90f),
                    new Vector3(1f, 2f, 1f))
            };
            LODDefinition[] lods =
            {
                new LODDefinition { mesh = first },
                new LODDefinition { mesh = second }
            };

            Bounds result = GPUOcclusionCulling.UnionInstanceBounds(instance, lods);

            Assert.That(result.min.x, Is.EqualTo(-4f).Within(0.0001f));
            Assert.That(result.max.x, Is.EqualTo(6f).Within(0.0001f));
            Assert.That(result.min.y, Is.EqualTo(-2f).Within(0.0001f));
            Assert.That(result.max.y, Is.EqualTo(2f).Within(0.0001f));
            Assert.That(result.min.z, Is.EqualTo(-1f).Within(0.0001f));
            Assert.That(result.max.z, Is.EqualTo(1f).Within(0.0001f));
        }
        finally
        {
            Object.DestroyImmediate(first);
            Object.DestroyImmediate(second);
        }
    }

    [TestCase(OcclusionCullingMode.Conservative, 0, 0.10f, 2, true)]
    [TestCase(OcclusionCullingMode.Balanced, 1, 0.05f, 1, true)]
    [TestCase(OcclusionCullingMode.Aggressive, 2, 0.02f, 1, false)]
    [TestCase(OcclusionCullingMode.Disabled, 0, 0f, 1, false)]
    public void QualityPresetMapping_IsStable(
        OcclusionCullingMode mode,
        int samplePattern,
        float bias,
        int historyUpdates,
        bool buildsPyramid)
    {
        OcclusionQualitySettings result = GPUOcclusionCulling.GetQualitySettings(mode);

        Assert.That(result.samplePattern, Is.EqualTo(samplePattern));
        Assert.That(result.depthBiasMeters, Is.EqualTo(bias).Within(0.0001f));
        Assert.That(result.occludedUpdatesBeforeRemoval, Is.EqualTo(historyUpdates));
        Assert.That(result.buildsDepthPyramid, Is.EqualTo(buildsPyramid));
    }

    [Test]
    public void Conservative_IsSerializedDefaultValue()
    {
        Assert.That((int)OcclusionCullingMode.Conservative, Is.Zero);
    }

    private static LodTransform TransformAt(
        Vector3 position,
        Quaternion rotation,
        Vector3 scale)
    {
        return new LodTransform
        {
            position = position,
            rotation = new Vector4(rotation.x, rotation.y, rotation.z, rotation.w),
            scale = scale
        };
    }
}
