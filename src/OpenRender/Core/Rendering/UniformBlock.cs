using OpenTK.Graphics.OpenGL4;
using System.Runtime.CompilerServices;

namespace OpenRender.Core.Rendering;

public class UniformBuffer<T> where T : struct
{
    private readonly int bindingPoint; 
    private readonly int bufferHandle;
    private readonly string uniformName;

    public UniformBuffer(string uniformName, int bindingPoint)
    {
        GL.CreateBuffers(1, out bufferHandle);

        // Bind buffer object to uniform buffer target
        GL.NamedBufferStorage(bufferHandle, Unsafe.SizeOf<T>(), IntPtr.Zero, BufferStorageFlags.MapWriteBit | BufferStorageFlags.DynamicStorageBit);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, bufferHandle, -1, $"UB {uniformName}");
        Log.CheckGlError();

        this.uniformName = uniformName;
        this.bindingPoint = bindingPoint;
    }

    public void BindToShaderProgram(Shader program)
    {
        var uniformBlockIndex = program.GetUniformBlockIndex(uniformName);
        GL.UniformBlockBinding(program.Handle, uniformBlockIndex, bindingPoint);
        GL.BindBufferRange(BufferRangeTarget.UniformBuffer, bindingPoint, bufferHandle, 0, Unsafe.SizeOf<T>());
    }

    public void UpdateSettings(ref T settings) => 
        GL.NamedBufferSubData(bufferHandle, IntPtr.Zero, Unsafe.SizeOf<T>(), ref settings);

    public bool IsUniformSupported(Shader program)
    {
        var uniformBlockIndex = program.GetUniformBlockIndex(uniformName);
        return uniformBlockIndex != -1;
    }
}
