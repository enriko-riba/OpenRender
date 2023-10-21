using System.Runtime.InteropServices;

namespace OpenRender.Core.Rendering;

[StructLayout(LayoutKind.Sequential)]
public struct DrawElementsIndirectCommand
{
    public int Count;
    public int InstanceCount;
    public int FirstIndex;
    public int BaseVertex;
    public int BaseInstance;
}