#ifndef SHADER_GRAPH_SUPPORT_H
#define SHADER_GRAPH_SUPPORT_H

// Many thanks to Refsa's Gist for the code!
// https://gist.github.com/Refsa/4949519af2160b9b29ea31d115de5dad

// You could also upload the model matrix
struct DrawData {
    float3 position;
    float4 rotation;
    float3 scale;
};

struct LodRenderData {
    float4 rotation;
    float4 scale;
    float4 offset; // LOD position minus the shared _Positions entry
};

StructuredBuffer<uint> _VisibleIndices;
// Start of this draw's (batch, LOD) region in _VisibleIndices (float: set per draw via MaterialPropertyBlock).
float _VisibleOffset;
StructuredBuffer<float4> _Positions;
StructuredBuffer<LodRenderData> _LodTransformData;

inline float4x4 TRSMatrix(float3 position, float4 rotation, float3 scale)
{
    float4x4 m = 0.0;

    m[0][0] = (1.0 - 2.0 * (rotation.y * rotation.y + rotation.z * rotation.z)) * scale.x;
    m[1][0] = (rotation.x * rotation.y + rotation.z * rotation.w) * scale.x * 2.0;
    m[2][0] = (rotation.x * rotation.z - rotation.y * rotation.w) * scale.x * 2.0;
    m[3][0] = 0.0;

    m[0][1] = (rotation.x * rotation.y - rotation.z * rotation.w) * scale.y * 2.0;
    m[1][1] = (1.0 - 2.0 * (rotation.x * rotation.x + rotation.z * rotation.z)) * scale.y;
    m[2][1] = (rotation.y * rotation.z + rotation.x * rotation.w) * scale.y * 2.0;
    m[3][1] = 0.0;

    m[0][2] = (rotation.x * rotation.z + rotation.y * rotation.w) * scale.z * 2.0;
    m[1][2] = (rotation.y * rotation.z - rotation.x * rotation.w) * scale.z * 2.0;
    m[2][2] = (1.0 - 2.0 * (rotation.x * rotation.x + rotation.y * rotation.y)) * scale.z;
    m[3][2] = 0.0;

    m[0][3] = position.x;
    m[1][3] = position.y;
    m[2][3] = position.z;
    m[3][3] = 1.0;

    return m;
}

inline void SetUnityMatrices(uint instanceID, inout float4x4 objectToWorld, inout float4x4 worldToObject)
{
#if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
    uint sourceIndex = _VisibleIndices[(uint)_VisibleOffset + instanceID];
    LodRenderData lodData = _LodTransformData[sourceIndex];

    DrawData drawData;
    drawData.position = _Positions[sourceIndex].xyz + lodData.offset.xyz;
    drawData.rotation = lodData.rotation;
    drawData.scale = lodData.scale.xyz;
  
    objectToWorld = mul(objectToWorld, TRSMatrix(drawData.position, drawData.rotation, drawData.scale));

    float3x3 w2oRotation;
    w2oRotation[0] = objectToWorld[1].yzx * objectToWorld[2].zxy - objectToWorld[1].zxy * objectToWorld[2].yzx;
    w2oRotation[1] = objectToWorld[0].zxy * objectToWorld[2].yzx - objectToWorld[0].yzx * objectToWorld[2].zxy;
    w2oRotation[2] = objectToWorld[0].yzx * objectToWorld[1].zxy - objectToWorld[0].zxy * objectToWorld[1].yzx;

    float det = dot(objectToWorld[0].xyz, w2oRotation[0]);
    w2oRotation = transpose(w2oRotation);
    w2oRotation *= rcp(det);
    float3 w2oPosition = mul(w2oRotation, -objectToWorld._14_24_34);

    worldToObject._11_21_31_41 = float4(w2oRotation._11_21_31, 0.0f);
    worldToObject._12_22_32_42 = float4(w2oRotation._12_22_32, 0.0f);
    worldToObject._13_23_33_43 = float4(w2oRotation._13_23_33, 0.0f);
    worldToObject._14_24_34_44 = float4(w2oPosition, 1.0f);
#endif
}

void passthroughVec3_float(in float3 In, out float3 Out)
{
    Out = In;
}

// Merged-submesh materials (see SubmeshMerger): vertex color = base color (a = texture weight),
// UV2 = (metallic, smoothness).
void ApplyVertexMaterial_float(in float3 BaseColor, in float Metallic, in float Smoothness,
    in float4 VertexColor, in float4 MaterialData, in float Enabled,
    out float3 BaseColorOut, out float MetallicOut, out float SmoothnessOut)
{
    BaseColorOut = BaseColor;
    MetallicOut = Metallic;
    SmoothnessOut = Smoothness;
    if (Enabled > 0.5)
    {
        BaseColorOut = lerp(1.0, BaseColor, VertexColor.a) * VertexColor.rgb;
        MetallicOut = MaterialData.x;
        SmoothnessOut = MaterialData.y;
    }
}

void setup()
{
#if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
    SetUnityMatrices(unity_InstanceID, unity_ObjectToWorld, unity_WorldToObject);
#endif
}

#endif
