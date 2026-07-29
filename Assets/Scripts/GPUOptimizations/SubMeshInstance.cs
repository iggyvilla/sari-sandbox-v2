// from ReverieJake https://gist.github.com/reveriejake/b668119ecdccf8bcb267f2ea303b830d

using UnityEngine;
using UnityEngine.Rendering;

public class SubMeshInstance
{
    public Material material;
    public ComputeBuffer argsBuffer;
    private uint[] args;

    public SubMeshInstance(uint instVertCount, uint startVertLocation, uint instStartLocation, Material material)
    {
        this.material = material;

        args = new uint[5] { 0, 0, 0, 0, 0 };
        args[0] = instVertCount;        // Vert Count per Instance
        args[1] = 0;                    // Instance Count
        args[2] = startVertLocation;    // Start Vertex Location
        args[3] = instStartLocation;    // Start Instance Location

        argsBuffer = new ComputeBuffer(1, args.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
        argsBuffer.SetData(args);
    }

    public void UpdateInstanceCountBuf(ComputeBuffer srcBuf)
    {
        ComputeBuffer.CopyCount(srcBuf, argsBuffer, sizeof(uint));
    }

    public void UpdateInstanceCountBuf(CommandBuffer cmd, ComputeBuffer srcBuf)
    {
        cmd.CopyCounterValue(srcBuf, argsBuffer, sizeof(uint));
    }
    
    public void UpdateInstanceCount(int instanceCount)
    {
        args[1] = (uint)instanceCount;
        argsBuffer.SetData(args);
    }

    public void Release()
    {
        if (argsBuffer != null)
            argsBuffer.Release();
        argsBuffer = null;
    }
}
