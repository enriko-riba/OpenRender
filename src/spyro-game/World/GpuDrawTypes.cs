using System.Runtime.InteropServices;
using OpenTK.Mathematics;

namespace SpyroGame.World;

// Shared GPU draw types for MDI pipeline
[StructLayout(LayoutKind.Sequential)]
public struct DrawDataGpu
{
    public Matrix4 Model;        // 64 bytes
    public Vector4 ChunkPos;     // 16 bytes
    public int BlocksBase;       // 4
    public int ChunkSize;        // 4
    public int OutlinedLocalIndex; // 4
    public int Enabled;          // 4 -> total 96 bytes
}

// Must match OpenGL's DrawElementsIndirectCommand exactly: 5 uints = 20 bytes
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DrawElementsIndirectCommand
{
    public uint Count;
    public uint InstanceCount;
    public uint FirstIndex;
    public uint BaseVertex;
    public uint BaseInstance;
}

