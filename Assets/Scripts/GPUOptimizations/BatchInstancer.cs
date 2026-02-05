using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

public struct DrawData {
    public Vector3 position;
    public Vector4 rotation;
    public Vector3 scale;
};

public class BatchInstancer : MonoBehaviour
{
    public Camera agentCamera;
    public Mesh instanceMesh;
    public Material[] materials;
    private SubMeshInstance[] subMeshInstances;
    
    /* custom shader buffers setup */
    
    /* holds DrawData for ALL items (of one type) to draw */
    private List<DrawData> instances = new();
    public string itemId;
    private ComputeBuffer _drawDataBuffer;
    private int drawDataSize = Marshal.SizeOf<DrawData>();
    
    private int _sDrawDataId;
    
    /* frustum culling compute shader setup */
    
    public ComputeShader frustumCullingShader;
    private ComputeBuffer _simplePlaneBuffer;
    private ComputeBuffer _unculledDataBuffer;
    
    private int simplePlaneSize = sizeof(float) * 4;
    
    private int _fDrawBufferId;
    private int _fNumToDrawId;
    private int _fPlanesId;
    private int _fUnculledBufferId;
    private int _fKernelId;

    private bool ready = false;

    public void Init()
    {
        subMeshInstances = new SubMeshInstance[materials.Length];
        
        for (int i = 0; i < materials.Length; i++)
        {
            subMeshInstances[i] = new SubMeshInstance(
                instanceMesh.GetIndexCount(i), 
                instanceMesh.GetIndexStart(i), 
                instanceMesh.GetBaseVertex(i), 
                materials[i]
            );
        }
        
        _simplePlaneBuffer = new ComputeBuffer(6, simplePlaneSize);
        
        _fDrawBufferId = Shader.PropertyToID("draw_buffer");
        _fNumToDrawId = Shader.PropertyToID("num_to_draw");
        _fPlanesId = Shader.PropertyToID("planes");
        _fUnculledBufferId = Shader.PropertyToID("unculled_buf");
        _fKernelId = frustumCullingShader.FindKernel("CSMain");
        
        _sDrawDataId = Shader.PropertyToID("_DrawData");
        
        frustumCullingShader.SetBuffer(_fKernelId, _fPlanesId, _simplePlaneBuffer);
        
        ready = true;
    }

    // LateUpdate becuse GPUInstanceTracker has to
    // calculate cameraFrustumPlanes first at Update()
    void Update()
    {
        // Continuously render batches only when Init() has been called at least once
        if (!ready) return;
        
        Profiler.BeginSample("Get Planes");
        // returns 6 planes
        SimplePlane[] planes = GPUInstanceTracker.Instance.cameraFrustumPlanes;
        _simplePlaneBuffer.SetData(planes);
        Profiler.EndSample();
        
        Profiler.BeginSample("Dispatch ComputeShader");
        _unculledDataBuffer.SetData(Array.Empty<DrawData>());
        _unculledDataBuffer.SetCounterValue(0);
        
        // dispatch ComputeShader
        frustumCullingShader.Dispatch(
            _fKernelId, 
            Mathf.CeilToInt(instances.Count/64f), 
            1, 
            1
        );
        Profiler.EndSample();
        
        for (int i = 0; i < subMeshInstances.Length; i++)
        {
            // tells the shader which items to render
            subMeshInstances[i].material.SetBuffer(_sDrawDataId, _unculledDataBuffer);
            // tells the shader how many of an item to render
            subMeshInstances[i].UpdateInstanceCountBuf(_unculledDataBuffer);
            
            // subMeshInstances[i].material.SetBuffer(_sDrawDataId, _drawDataBuffer);
            // subMeshInstances[i].UpdateInstanceCount(instances.Count);
            
            // i is the submeshIndex 
            Graphics.DrawMeshInstancedIndirect(
                instanceMesh, 
                i, 
                subMeshInstances[i].material, 
                new Bounds(Vector3.zero, Vector3.one * 1000f),
                subMeshInstances[i].argsBuffer
            );
        }
    }
    
    void OnDestroy()
    {
        /*
         * Destroy ComputeBuffers
         * C# doesn't handle the cleanup of these
         * since they are in the GPU
         */ 
        foreach (var t in subMeshInstances)
            t.Release();

        _drawDataBuffer?.Release();
        _simplePlaneBuffer?.Release();
        _unculledDataBuffer?.Release();
    }
    
    public void AddObjectToBatch(DrawData d, float itemHeight)
    {
        // if mesh pivot point isn't at its center
        if (instanceMesh.bounds.center == Vector3.zero)
        {
            /*
             * our shelf pos calculations assume the pivot
             * is at the items bottom, not center, so adjust
             * for it, without this, items spawn IN the shelves,
             * not ON
             */
            d.position.y += itemHeight/2;
        }
        
        instances.Add(d);
        
        // TODO: Might have a better solution but it works for now
        _drawDataBuffer?.Release();
        _drawDataBuffer = new ComputeBuffer(instances.Count, drawDataSize);
        _drawDataBuffer.SetData(instances);
        
        _unculledDataBuffer?.Release();
        _unculledDataBuffer = new ComputeBuffer(instances.Count, drawDataSize, ComputeBufferType.Append);

        if (frustumCullingShader is null)
        {
            Debug.LogWarning("frustumCullingShader is null");
            return;
        }
        
        // update no. of items to draw in culling compute shader
        frustumCullingShader.SetInt(_fNumToDrawId, instances.Count);
        
        // stores all items
        frustumCullingShader.SetBuffer(_fKernelId, _fDrawBufferId, _drawDataBuffer);
        
        // set buffer: stores unculled items 
        frustumCullingShader.SetBuffer(_fKernelId, _fUnculledBufferId, _unculledDataBuffer);
    }
}
