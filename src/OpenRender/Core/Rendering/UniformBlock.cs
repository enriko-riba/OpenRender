using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core.Rendering;

public class UniformBuffer<T> where T : struct, ISize
{
    private readonly int bindingPoint; 
    private readonly int bufferHandle;
    private readonly string uniformName;

    public UniformBuffer(string uniformName, int bindingPoint)
    {
        GL.GenBuffers(1, out bufferHandle);

        // Bind buffer object to uniform buffer target
        GL.BindBuffer(BufferTarget.UniformBuffer, bufferHandle);
        GL.BufferData(BufferTarget.UniformBuffer, T.Size, IntPtr.Zero, BufferUsageHint.DynamicDraw);

        // Bind buffer object to binding point
        GL.BindBufferBase(BufferRangeTarget.UniformBuffer, this.bindingPoint, bufferHandle);
        this.uniformName = uniformName;
        this.bindingPoint = bindingPoint;
    }

    public void BindToShaderProgram(Shader program)
    {
        var uniformBlockIndex = program.GetUniformBlockIndex(uniformName);
        GL.UniformBlockBinding(program.Handle, uniformBlockIndex, bindingPoint);
        GL.BindBufferRange(BufferRangeTarget.UniformBuffer, bindingPoint, bufferHandle, 0, T.Size);
    }

    public void UpdateSettings(ref T settings)
    {
        GL.BindBuffer(BufferTarget.UniformBuffer, bufferHandle);
        GL.BufferSubData(BufferTarget.UniformBuffer, 0, T.Size, ref settings);
    }
}
