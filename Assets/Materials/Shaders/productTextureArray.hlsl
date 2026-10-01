#ifndef PRODUCT_TEXTURE_ARRAY_H
#define PRODUCT_TEXTURE_ARRAY_H

// Albedo for URPLit_Procedural. Classic mode (_TextureArray off) samples _BaseMap like a normal Sample Texture 2D.
// Array mode samples the packed product texture array (ProductTextureAtlas) from per-vertex data written by
// SubmeshMerger. UV already maps into the texture's region of the layer.
//   MaterialData (UV2): z = layer (< 0: untextured), w = maxMip + 16 * clampU + 32 * clampV
//   Region (UV3):       x, y, width, height of the region in layer UV
// Axes without clamp bits span the whole layer, so hardware Repeat wraps them like the original texture.

float _ProductLayerSize;  // set by ProductTextureAtlas (1024, 2048 or 4096)
float _ProductAnisoMax;   // set by ProductTextureAtlas (effective max anisotropy)

void SampleProductAlbedo_float(UnityTexture2D BaseMap, float2 UV, UnityTexture2DArray BaseMapArray,
    float4 MaterialData, float4 Region, float Enabled, out float4 Out)
{
    Out = 1.0;
    if (Enabled < 0.5)
    {
        Out = SAMPLE_TEXTURE2D(BaseMap.tex, BaseMap.samplerstate, BaseMap.GetTransformedUV(UV));
        return;
    }

    // Gradients come from the unclamped UV so mip selection ignores the clamping below.
    float2 dx = ddx(UV);
    float2 dy = ddy(UV);
    float2 texelDx = dx * _ProductLayerSize;
    float2 texelDy = dy * _ProductLayerSize;

    // Level the sampler will pick: footprint length over the anisotropy ratio (measured on Metal: continuous, not rounded up).
    float lenX = dot(texelDx, texelDx);
    float lenY = dot(texelDy, texelDy);
    float rMax = sqrt(max(max(lenX, lenY), 1e-12));
    float rMin = sqrt(max(min(lenX, lenY), 1e-12));
    float probes = min(rMax / rMin, max(_ProductAnisoMax, 1.0));
    float lod = log2(rMax / probes);

    int flags = (int)(MaterialData.w + 0.5);
    int maxMip = flags & 15;
    bool clampU = (flags & 16) != 0;
    bool clampV = (flags & 32) != 0;

    // Shrink the gradients (keeps their ratio) until the sampler stays on copied data:
    // - mips below one 4x4 block of the region were never copied;
    // - the footprint (anisotropic probes included) must fit inside the region on clamped axes,
    //   or it would read the neighbouring regions / empty layer space.
    float2 extent = abs(texelDx) + abs(texelDy);
    float2 fit = Region.zw * _ProductLayerSize / max(extent, 1e-6);
    float shrink = min(1.0, exp2(maxMip - lod));
    shrink = min(shrink, min(clampU ? fit.x : 1e6, clampV ? fit.y : 1e6));
    dx *= shrink;
    dy *= shrink;
    texelDx *= shrink;
    texelDy *= shrink;

    if (MaterialData.z < -0.5)
        return;

    // Keep the whole filter footprint (bilinear texel at the chosen mip + anisotropic probes) inside the region.
    float2 inset = 0.5 * max(1.0, abs(texelDx) + abs(texelDy));
    float2 insetUV = min(inset / _ProductLayerSize, 0.5 * Region.zw);
    float2 clamped = clamp(UV, Region.xy + insetUV, Region.xy + Region.zw - insetUV);
    float2 uv = float2(clampU ? clamped.x : UV.x, clampV ? clamped.y : UV.y);

    Out = SAMPLE_TEXTURE2D_ARRAY_GRAD(BaseMapArray.tex, BaseMapArray.samplerstate, uv, (int)(MaterialData.z + 0.5), dx, dy);
}

#endif
