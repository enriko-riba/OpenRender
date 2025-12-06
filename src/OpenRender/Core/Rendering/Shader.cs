using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace OpenRender.Core.Rendering;

/// <summary>
/// A simple shader program builder.
/// </summary>
public partial class Shader
{
    private static readonly Dictionary<string, Shader> shaderCache = [];

    private readonly Dictionary<string, int> uniformLocations = [];
    private readonly Dictionary<string, int> uniformBlockIndices = [];
    public readonly int Handle;
    private readonly string DebugName;

    /// <summary>
    /// Creates a new Program from vertex and fragment shaders.
    /// </summary>
    /// <param name="vertPath"></param>
    /// <param name="fragPath"></param>
    public Shader(string vertPath, string fragPath)
    {
        var cacheKey = $"{vertPath}|{fragPath}";
        if (shaderCache.TryGetValue(cacheKey, out var cachedShader))
        {
            Handle = cachedShader.Handle;
            uniformLocations = cachedShader.uniformLocations;
            uniformBlockIndices = cachedShader.uniformBlockIndices;
            DebugName = cachedShader.DebugName;
            return;
        }

        Log.Info("creating program '{0}', '{1}'", vertPath, fragPath);
        Log.Debug("creating vertex shader...");
        var shaderSource = ReadShaderText(vertPath);
        var vertexShader = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(vertexShader, shaderSource);
        CompileShader(vertexShader, vertPath);
        Log.CheckGlError();

        Log.Debug("creating fragment shader...");
        shaderSource = ReadShaderText(fragPath);
        var fragmentShader = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fragmentShader, shaderSource);
        CompileShader(fragmentShader, fragPath);
        Log.CheckGlError();

        // create the program
        Handle = GL.CreateProgram();
        DebugName = $"{Handle}: '{vertPath}','{fragPath}'";

        GL.AttachShader(Handle, vertexShader);
        GL.AttachShader(Handle, fragmentShader);
        LinkProgram(Handle);
        Log.CheckGlError();

        // Detach and then delete individual shaders - they are not needed anymore
        Log.Debug("deleting shader programs: {0}, {1}", vertexShader, fragmentShader);
        GL.DetachShader(Handle, vertexShader);
        GL.DetachShader(Handle, fragmentShader);
        GL.DeleteShader(fragmentShader);
        GL.DeleteShader(vertexShader);
        Log.CheckGlError();

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
        Log.Debug("active uniform blocks: {0} -> {1}", numberOfUniformBlocks, uniformBlockIndices.Count > 0 ? string.Join(", ", uniformBlockIndices.Keys) : "n/a");

        Log.Info("created program {0}", DebugName);
        Log.CheckGlError();

        shaderCache.Add(cacheKey, this);
    }

    public Shader(string path, ShaderType shaderType)
    {
        var cacheKey = $"{path}";
        if (shaderCache.TryGetValue(cacheKey, out var cachedShader))
        {
            Handle = cachedShader.Handle;
            uniformLocations = cachedShader.uniformLocations;
            uniformBlockIndices = cachedShader.uniformBlockIndices;
            DebugName = cachedShader.DebugName;
            return;
        }

        var shaderObject = GL.CreateShader(shaderType);
        var shaderSource = ReadShaderText(path);
        GL.ShaderSource(shaderObject, shaderSource);
        CompileShader(shaderObject, path);
        Log.CheckGlError();

        // create the program
        Handle = GL.CreateProgram();
        GL.AttachShader(Handle, shaderObject);
        LinkProgram(Handle);
        Log.CheckGlError();

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
        Log.Debug("active uniform blocks: {0} -> {1}", numberOfUniformBlocks, uniformBlockIndices.Count > 0 ? string.Join(", ", uniformBlockIndices.Keys) : "n/a");

        DebugName = $"{Handle}: '{path}'";

        Log.Info("created program {0}", DebugName);
        Log.CheckGlError();

        shaderCache.Add(cacheKey, this);
    }

    public Shader(string computeSource)
    {
        var cacheKey = $"compute:{computeSource}";
        if (shaderCache.TryGetValue(cacheKey, out var cachedShader))
        {
            Handle = cachedShader.Handle;
            uniformLocations = cachedShader.uniformLocations;
            uniformBlockIndices = cachedShader.uniformBlockIndices;
            DebugName = cachedShader.DebugName;
            return;
        }

        var computeShader = GL.CreateShader(ShaderType.ComputeShader);
        GL.ShaderSource(computeShader, computeSource);
        CompileShader(computeShader, computeSource);
        Log.CheckGlError();

        // create the program
        Handle = GL.CreateProgram();
        GL.AttachShader(Handle, computeShader);
        LinkProgram(Handle);
        Log.CheckGlError();

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
        Log.Debug("active uniform blocks: {0} -> {1}", numberOfUniformBlocks, uniformBlockIndices.Count > 0 ? string.Join(", ", uniformBlockIndices.Keys) : "n/a");

        DebugName = $"{Handle}: '{computeShader}'";

        Log.Info("created program {0}", DebugName);
        Log.CheckGlError();

        shaderCache.Add(cacheKey, this);
    }

    public override string ToString() => DebugName;

    /// <summary>
    /// Uses this program (just invokes GL.UseProgram).
    /// </summary>
    public void Use() => GL.UseProgram(Handle);

    /// <summary>
    /// Returns the index of the named uniform block.
    /// </summary>
    /// <param name="uniformBlockName"></param>
    /// <returns></returns>
    public int GetUniformBlockIndex(string uniformBlockName) => uniformBlockIndices.TryGetValue(uniformBlockName, out var index) ? index : -1;

    /// <summary>
    /// Returns the index or location of the named uniform.
    /// </summary>
    /// <param name="uniformName"></param>
    /// <returns></returns>
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
    public int GetAttributeLocation(string attribName) => GL.GetAttribLocation(Handle, attribName);

    /// <summary>
    /// Sets a uniform int.
    /// </summary>
    /// <param name="name">The name of the uniform</param>
    /// <param name="data">The data to set</param>
    public void SetInt(string name, int data)
    {
        GL.UseProgram(Handle);
        if (IsUniformValid(name)) GL.Uniform1(uniformLocations[name], data);
    }

    /// <summary>
    /// Sets a uniform unsigned int.
    /// </summary>
    /// <param name="name">The name of the uniform</param>
    /// <param name="data">The data to set</param>
    public void SetUInt(string name, uint data)
    {
        GL.UseProgram(Handle);
        if (IsUniformValid(name)) GL.Uniform1(uniformLocations[name], data);
    }

    /// <summary>
    /// Sets a uniform float.
    /// </summary>
    /// <param name="name">The name of the uniform</param>
    /// <param name="data">The data to set</param>
    public void SetFloat(string name, float data)
    {
        GL.UseProgram(Handle);
        if (IsUniformValid(name)) GL.Uniform1(uniformLocations[name], data);
    }

    /// <summary>
    /// Sets a uniform Matrix4.
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
    /// Sets a uniform Vector4.
    /// </summary>
    /// <param name="name">The name of the uniform</param>
    /// <param name="data">The data to set</param>
    public void SetVector4(string name, ref Vector4 data)
    {
        GL.UseProgram(Handle);
        if (IsUniformValid(name)) GL.Uniform4(uniformLocations[name], data);
    }

    /// <summary>
    /// Sets a uniform4 values.
    /// </summary>
    /// <param name="name">The name of the uniform</param>
    /// <param name="data">The data to set</param>
    public void SetUniform4(string name, float v1, float v2, float v3, float v4)
    {
        GL.UseProgram(Handle);
        if (IsUniformValid(name)) GL.Uniform4(uniformLocations[name], v1, v2, v3, v4);
    }

    /// <summary>
    /// Sets a uniform Vector3.
    /// </summary>
    /// <param name="name">The name of the uniform</param>
    /// <param name="data">The data to set</param>
    public void SetVector3(string name, ref Vector3 data)
    {
        GL.UseProgram(Handle);
        if (IsUniformValid(name)) GL.Uniform3(uniformLocations[name], data);
    }

    /// <summary>
    /// Sets a uniform Vector2.
    /// </summary>
    /// <param name="name">The name of the uniform</param>
    /// <param name="data">The data to set</param>
    public void SetVector2(string name, ref Vector2 data)
    {
        GL.UseProgram(Handle);
        if (IsUniformValid(name)) GL.Uniform2(uniformLocations[name], data);
    }

    /// <summary>
    /// Sets a uniform Vector2i.
    /// </summary>
    /// <param name="name">The name of the uniform</param>
    /// <param name="data">The data to set</param>
    public void SetVector2(string name, ref Vector2i data)
    {
        GL.UseProgram(Handle);
        if (IsUniformValid(name)) GL.Uniform2(uniformLocations[name], data);
    }

    public bool UniformBlockExists(string name) => uniformBlockIndices.ContainsKey(name);

    public bool UniformExists(string name) => uniformLocations.ContainsKey(name);

    private bool IsUniformValid(string name)
    {
        if (!UniformExists(name))
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

    private static string ReadShaderText(string path) => ReadShaderTextInternal(path, []);

    private static string ReadShaderTextInternal(string path, HashSet<string> includedFiles)
    {
        var fullPath = Path.GetFullPath(path);
        if (includedFiles.Contains(fullPath))
        {
            return "";
        }
        includedFiles.Add(fullPath);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Shader include file not found: {fullPath}");
        }

        // Read raw bytes
        var bytes = File.ReadAllBytes(fullPath);

        // Strip UTF-8 BOM if present
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            bytes = bytes[3..];

        // Decode as UTF-8 (don’t throw on invalid bytes)
        var src = System.Text.Encoding.UTF8.GetString(bytes);

        // Remove any zero-width BOMs anywhere in the file, not just start
        src = src.Replace("\uFEFF", string.Empty);

        // Normalize Windows line endings to Unix
        src = src.Replace("\r\n", "\n").Replace("\r", "\n");

        // Remove any embedded NULs (can happen with copy/paste or toolchains)
        src = src.Replace("\0", string.Empty);

        // Ensure the shader ends with a newline
        if (!src.EndsWith("\n"))
            src += "\n";

        // Sanitize Unicode in comments
        src = SanitizeComments(src);

        // Process Includes
        var sb = new System.Text.StringBuilder();
        using (var reader = new StringReader(src)!)
        {
            string line;
            var inBlockComment = false;
            while ((line = reader!.ReadLine()) != null)
            {
                var trimmed = line.Trim();

                // Simple block comment tracking
                if (!inBlockComment && trimmed.StartsWith("/*"))
                {
                    inBlockComment = true;
                }
                
                if (inBlockComment)
                {
                    if (trimmed.EndsWith("*/")) inBlockComment = false;
                    sb.AppendLine(line);
                    continue;
                }

                if (trimmed.StartsWith("//"))
                {
                    sb.AppendLine(line);
                    continue;
                }

                if (trimmed.StartsWith("#include"))
                {
                    var match = IncludeFileRegex().Match(trimmed);
                    if (match.Success)
                    {
                        var includeFile = match.Groups[1].Value;
                        var dir = Path.GetDirectoryName(fullPath);
                        var includePath = Path.Combine(dir ?? "", includeFile);
                        sb.AppendLine(ReadShaderTextInternal(includePath, includedFiles));
                    }
                    else
                    {
                        sb.AppendLine(line);
                    }
                }
                else
                {
                    sb.AppendLine(line);
                }
            }
        }

        return sb.ToString();
    }

    private static string SanitizeComments(string src) =>
        // Regex to find comments: //... or /* ... */
        SanitizeRegex().Replace(src, match =>
        {
            var text = match.Value;
            var sb = new System.Text.StringBuilder(text.Length);
            foreach (var c in text)
            {
                // Keep ASCII characters, replace others with '?'
                if (c <= 127) sb.Append(c);
                else sb.Append('?');
            }
            return sb.ToString();
        });
    [System.Text.RegularExpressions.GeneratedRegex("#include\\s+\"([^\"]+)\"")]
    private static partial System.Text.RegularExpressions.Regex IncludeFileRegex();
    [System.Text.RegularExpressions.GeneratedRegex(@"(\/\/.*)|(\/\*[\s\S]*?\*\/)")]
    private static partial System.Text.RegularExpressions.Regex SanitizeRegex();
}