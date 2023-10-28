using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core.Rendering;

public class UniformBuffer<T> where T : struct//, ISize
{
    private readonly int bindingPoint; 
    private readonly int bufferHandle;
    private readonly string uniformName;
    private T? data;

    public UniformBuffer(string uniformName, int bindingPoint)
    {
        GL.CreateBuffers(1, out bufferHandle);

        // Bind buffer object to uniform buffer target
        //GL.BindBuffer(BufferTarget.UniformBuffer, bufferHandle);
        //GL.BufferData(BufferTarget.UniformBuffer, T.Size, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.NamedBufferStorage(bufferHandle, System.Runtime.CompilerServices.Unsafe.SizeOf<T>(), IntPtr.Zero, BufferStorageFlags.MapWriteBit | BufferStorageFlags.DynamicStorageBit);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, bufferHandle, -1, $"UB {uniformName}");

        // Bind buffer object to binding point
        //GL.BindBufferBase(BufferRangeTarget.UniformBuffer, bindingPoint, bufferHandle);
        
        Log.CheckGlError();

        this.uniformName = uniformName;
        this.bindingPoint = bindingPoint;
    }

    public void BindToShaderProgram(Shader program)
    {
        var uniformBlockIndex = program.GetUniformBlockIndex(uniformName);
        GL.UniformBlockBinding(program.Handle, uniformBlockIndex, bindingPoint);
        GL.BindBufferRange(BufferRangeTarget.UniformBuffer, bindingPoint, bufferHandle, 0, System.Runtime.CompilerServices.Unsafe.SizeOf<T>());// T.Size);
    }

    public void UpdateSettings(ref T settings)
    {
        //GL.BindBuffer(BufferTarget.UniformBuffer, bufferHandle);
        //GL.BufferSubData(BufferTarget.UniformBuffer, 0, T.Size, ref settings);
        GL.NamedBufferSubData(bufferHandle, IntPtr.Zero, System.Runtime.CompilerServices.Unsafe.SizeOf<T>(), ref settings);
        data = settings;
    }

    public T? Data => data;

    public bool IsUniformSupported(Shader program)
    {
        var uniformBlockIndex = program.GetUniformBlockIndex(uniformName);
        return uniformBlockIndex != -1;
    }
}
