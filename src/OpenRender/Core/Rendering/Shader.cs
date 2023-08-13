using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace OpenRender.Core.Rendering;

/// <summary>
/// A simple shader program builder.
/// </summary>
public class Shader
{
    private readonly Dictionary<string, int> uniformLocations = new();
    private readonly Dictionary<string, int> uniformBlockIndices = new();
    public readonly int Handle;

    /// <summary>
    /// Creates a new Program from vertex and fragment shaders.
    /// </summary>
    /// <param name="vertPath"></param>
    /// <param name="fragPath"></param>
    public Shader(string vertPath, string fragPath)
    {
        Log.Info("creating program '{0}', '{1}'", vertPath, fragPath);
        Log.Debug("creating vertex shader...");
        var shaderSource = File.ReadAllText(vertPath);
        var vertexShader = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(vertexShader, shaderSource);
        CompileShader(vertexShader, vertPath);

        Log.Debug("creating fragment shader...");
        shaderSource = File.ReadAllText(fragPath);
        var fragmentShader = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fragmentShader, shaderSource);
        CompileShader(fragmentShader, fragPath);

        // create the program
        Handle = GL.CreateProgram();
        GL.AttachShader(Handle, vertexShader);
        GL.AttachShader(Handle, fragmentShader);
        LinkProgram(Handle);

        // Detach and then delete individual shaders - they are not needed anymore
        Log.Debug("deleting shader programs: {0}, {1}", vertexShader, fragmentShader);
        GL.DetachShader(Handle, vertexShader);
        GL.DetachShader(Handle, fragmentShader);
        GL.DeleteShader(fragmentShader);
        GL.DeleteShader(vertexShader);

        // cache all uniform locations, querying them is slow
        GL.GetProgram(Handle, GetProgramParameterName.ActiveUniforms, out var numberOfUniforms);                        
        for (var i = 0; i < numberOfUniforms; i++)
        {
            var key = GL.GetActiveUniform(Handle, i, out _, out _);
            var location = GL.GetUniformLocation(Handle, key);
            uniformLocations.Add(key, location);
        }
        Log.Debug("active uniforms: {0} -> {1}", numberOfUniforms, string.Join(", ", uniformLocations.Keys));

        GL.GetProgram(Handle, GetProgramParameterName.ActiveUniformBlocks, out var numberOfUniformBlocks);            
        for (var i = 0; i < numberOfUniformBlocks; i++)
        {
            GL.GetActiveUniformBlockName(Handle, i, 256, out _, out var key);
            var idx = GL.GetUniformBlockIndex(Handle, key);
            uniformBlockIndices.Add(key, idx);
        }
        Log.Debug("active uniform blocks: {0} -> {1}", numberOfUniformBlocks, string.Join(", ", uniformBlockIndices.Keys));

        Log.Info("created program {0}", Handle);
    }

    /// <summary>
    /// Uses this program (just invokes GL.UseProgram).
    /// </summary>
    public void Use() => GL.UseProgram(Handle);

    /// <summary>
    /// Returns the index of the named uniform block.
    /// </summary>
    /// <param name="uniformBlockName"></param>
    /// <returns></returns>
    public int GetUniformBlockIndex(string uniformBlockName)
    {
        uniformBlockIndices.TryGetValue(uniformBlockName, out var index);
        return index;
    }

    public int GetUniformLocation(string uniformName)
    {
        uniformLocations.TryGetValue(uniformName, out var index);
        return index;
    }


    /// <summary>
    /// Queries the program for attribute location.
    /// </summary>
    /// <param name="attribName"></param>
    /// <returns></returns>
    public int GetAttributeLocation(string attribName)
    {
        return GL.GetAttribLocation(Handle, attribName);
    }

    /// <summary>
    /// Set a uniform int.
    /// </summary>
    /// <param name="name">The name of the uniform</param>
    /// <param name="data">The data to set</param>
    public void SetInt(string name, int data)
    {
        GL.UseProgram(Handle);
        if (IsUniformValid(name)) GL.Uniform1(uniformLocations[name], data);
    }

    /// <summary>
    /// Set a uniform float.
    /// </summary>
    /// <param name="name">The name of the uniform</param>
    /// <param name="data">The data to set</param>
    public void SetFloat(string name, float data)
    {
        GL.UseProgram(Handle);
        if (IsUniformValid(name)) GL.Uniform1(uniformLocations[name], data);
    }

    /// <summary>
    /// Set a uniform Matrix4.
    /// </summary>
    /// <param name="name">The name of the uniform</param>
    /// <param name="data">The data to set</param>
    /// <remarks>
    ///   <para>
    ///   The matrix is transposed before being sent to the shader.
    ///   </para>
    /// </remarks>
    public void SetMatrix4(string name, ref Matrix4 data)
    {
        GL.UseProgram(Handle);
        if (IsUniformValid(name)) GL.UniformMatrix4(uniformLocations[name], false, ref data);
    }

    /// <summary>
    /// Set a uniform Vector4.
    /// </summary>
    /// <param name="name">The name of the uniform</param>
    /// <param name="data">The data to set</param>
    public void SetVector4(string name, ref Vector4 data)
    {
        GL.UseProgram(Handle);
        if (IsUniformValid(name)) GL.Uniform4(uniformLocations[name], data);
    }

    /// <summary>
    /// Set a uniform Vector3.
    /// </summary>
    /// <param name="name">The name of the uniform</param>
    /// <param name="data">The data to set</param>
    public void SetVector3(string name, ref Vector3 data)
    {
        GL.UseProgram(Handle);
        if (IsUniformValid(name)) GL.Uniform3(uniformLocations[name], data);
    }

    /// <summary>
    /// Set a uniform Vector2.
    /// </summary>
    /// <param name="name">The name of the uniform</param>
    /// <param name="data">The data to set</param>
    public void SetVector2(string name, ref Vector2 data)
    {
        GL.UseProgram(Handle);
        if (IsUniformValid(name)) GL.Uniform2(uniformLocations[name], data);
    }

    public bool UniformExists(string name) => uniformLocations.ContainsKey(name);
    private bool IsUniformValid(string name)
    {
        if (!uniformLocations.ContainsKey(name))
        {
            Log.Warn($"uniform: '{name}' not found in program {Handle}!");
            return false;
        }
        return true;
    }


    private static void CompileShader(int shader, string path)
    {
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out var code);
        if (code != (int)All.True)
        {
            var infoLog = GL.GetShaderInfoLog(shader);
            Log.Error($"error compiling Shader({shader}@{path}).\n\n{infoLog}");
            throw new Exception($"Error compiling Shader({shader}@{path}).\n\n{infoLog}");
        }
        else
        {
            Log.Debug($"compiled shader: {shader}");
        }
    }

    private static void LinkProgram(int program)
    {
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var code);
        if (code != (int)All.True)
        {
            var infoLog = GL.GetProgramInfoLog(program);
            Log.Error($"error linking Program({program}).\n\n{infoLog}");
            throw new Exception($"Error linking Program({program}).\n\n{infoLog}");
        }
        else
        {
            Log.Debug($"linked program: {program}");
        }
    }

}