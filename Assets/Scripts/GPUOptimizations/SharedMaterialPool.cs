using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

// Gives products with identical instancing materials the same Material, so draws that share one don't make the
// renderer change state. Only valid when materials hold no per-product data (see InstanceCullingSystem.IndirectArgs).
public sealed class SharedMaterialPool : IDisposable
{
    private readonly Dictionary<string, Material> _materials = new();
    private readonly StringBuilder _key = new();

    public int Count => _materials.Count;

    // The pooled material equal to `material` (shader, queue, keywords, property values), adopting it when new.
    // The caller must own `material`: a duplicate is destroyed.
    public Material Intern(Material material)
    {
        string key = Signature(material);
        if (_materials.TryGetValue(key, out Material shared) && shared != null)
        {
            if (shared != material) Destroy(material);
            return shared;
        }

        _materials[key] = material;
        return material;
    }

    public void Dispose()
    {
        foreach (Material m in _materials.Values) Destroy(m);
        _materials.Clear();
    }

    private string Signature(Material m)
    {
        Shader shader = m.shader;
        _key.Clear().Append(shader.GetInstanceID()).Append('|').Append(m.renderQueue).Append('|');
        string[] keywords = m.shaderKeywords;
        Array.Sort(keywords, StringComparer.Ordinal);
        _key.AppendJoin(' ', keywords);

        for (int i = 0, n = shader.GetPropertyCount(); i < n; i++)
        {
            string name = shader.GetPropertyName(i);
            _key.Append('|');
            switch (shader.GetPropertyType(i))
            {
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range:
                    _key.Append(BitConverter.SingleToInt32Bits(m.GetFloat(name)));
                    break;
                case ShaderPropertyType.Int:
                    _key.Append(m.GetInteger(name));
                    break;
                case ShaderPropertyType.Color:
                case ShaderPropertyType.Vector:
                    Vector4 v = m.GetVector(name);
                    _key.Append(BitConverter.SingleToInt32Bits(v.x)).Append(',').Append(BitConverter.SingleToInt32Bits(v.y))
                        .Append(',').Append(BitConverter.SingleToInt32Bits(v.z)).Append(',').Append(BitConverter.SingleToInt32Bits(v.w));
                    break;
                default: // textures, with their tiling/offset
                    Texture t = m.GetTexture(name);
                    Vector2 scale = m.GetTextureScale(name), offset = m.GetTextureOffset(name);
                    _key.Append(t != null ? t.GetInstanceID() : 0).Append(',')
                        .Append(BitConverter.SingleToInt32Bits(scale.x)).Append(',').Append(BitConverter.SingleToInt32Bits(scale.y)).Append(',')
                        .Append(BitConverter.SingleToInt32Bits(offset.x)).Append(',').Append(BitConverter.SingleToInt32Bits(offset.y));
                    break;
            }
        }
        return _key.ToString();
    }

    private static void Destroy(UnityEngine.Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) UnityEngine.Object.Destroy(o);
        else UnityEngine.Object.DestroyImmediate(o);
    }
}
