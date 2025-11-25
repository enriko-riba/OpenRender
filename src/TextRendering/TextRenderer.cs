using OpenRender.Core.Rendering;
using OpenRender.Core.Rendering.Text;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SixLabors.Fonts;

namespace OpenRender.Text;

public sealed class TextRenderer : ITextRenderer
{
    private readonly int vao;
    private readonly int vbo;
    private readonly Shader shader;
    private readonly IFontAtlas fontAtlas;
    private readonly int BaseFontSize;
    private Matrix4 projectionMatrix;
    
    // Dynamic buffer management with improved growth strategy
    private const int InitialBufferSize = 4096; // 4KB - enough for ~256 characters
    private const float GrowthFactor = 1.5f;
    private const int MinBufferSize = 1024; // 1KB minimum
    private const int MaxBufferSize = 1048576; // 1MB maximum (safety limit)
    
    private int currentVboSize = InitialBufferSize * sizeof(float);
    private int peakVboUsage = 0; // Track peak usage for shrinking decisions
    private int framesSinceLastResize = 0;
    private float[] vertexBuffer = new float[InitialBufferSize];

    // Text caching for improved performance
    private readonly Dictionary<string, CachedText> textCache = new();
    private long currentFrame = 0;
    private const int CacheMaxEntries = 100;
    private const int CacheEvictionFrames = 180; // ~3 seconds at 60 FPS

    /// <summary>
    /// Cached text entry with pre-built geometry
    /// </summary>
    private class CachedText
    {
        public string Text { get; init; } = string.Empty;
        public int FontSize { get; init; }
        public float[] Vertices { get; init; } = Array.Empty<float>();
        public int VertexCount { get; init; }
        public long LastUsedFrame { get; set; }
        public float OriginalX { get; init; }
        public float OriginalY { get; init; }
    }

    public TextRenderer(Matrix4 projection, IFontAtlas fontAtlas)
    {
        projectionMatrix = projection;
        this.fontAtlas = fontAtlas;

        GL.CreateVertexArrays(1, out vao);
        GL.CreateBuffers(1, out vbo);
        GL.VertexArrayVertexBuffer(vao, 0, vbo, 0, 4 * sizeof(float));

        GL.EnableVertexArrayAttrib(vao, 0);
        GL.VertexArrayAttribFormat(vao, 0, 2, VertexAttribType.Float, false, 0);
        GL.VertexArrayAttribBinding(vao, 0, 0);

        GL.EnableVertexArrayAttrib(vao, 3);
        GL.VertexArrayAttribFormat(vao, 3, 2, VertexAttribType.Float, false, 2 * sizeof(float));
        GL.VertexArrayAttribBinding(vao, 3, 0);
        
        // Allocate initial buffer with dynamic storage
        GL.NamedBufferData(vbo, currentVboSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        Log.CheckGlError();

        GL.BindVertexArray(vao);
        shader = new Shader("Shaders/text.vert", "Shaders/text.frag");
        shader.Use();

        // Set the font atlas texture as a uniform in the shader
        GL.Uniform1(shader.GetUniformLocation("fontAtlasSampler"), 31);
        BaseFontSize = (int)MathF.Round(fontAtlas.TextOptions.Font.Size);
    }

    public IFontAtlas FontAtlas => fontAtlas;

    public Matrix4 Projection { get => projectionMatrix; set => projectionMatrix = value; }

    public static Matrix4 CreateTextRenderingProjection(float screenWidth, float screenHeight) => 
        Matrix4.CreateOrthographicOffCenter(0, screenWidth, screenHeight, 0, -1, 1);

    public Core.Rectangle Measure(string text)
    {
        var rect = TextMeasurer.MeasureAdvance(text, fontAtlas.TextOptions);
        return new Core.Rectangle(0, 0, (int)Math.Ceiling(rect.Width), (int)Math.Ceiling(rect.Height));
    }

    public Core.Rectangle Measure(string text, int fontSize)
    {
        var customOptions = new TextOptions(fontAtlas.TextOptions);
        var font = new Font(customOptions.Font, fontSize);
        customOptions.Font = font;
        var rect = TextMeasurer.MeasureAdvance(text, customOptions);
        return new Core.Rectangle(0, 0, (int)Math.Ceiling(rect.Width), (int)Math.Ceiling(rect.Height));
    }

    public void Render(string text, float x, float y, Vector3 color) => Render(text, BaseFontSize, x, y, color);

    public void Render(string text, int fontSize, float x, float y, Vector3 color)
    {
        if (string.IsNullOrEmpty(text)) return;

        currentFrame++;
        EvictOldCacheEntries();

        var matrix = projectionMatrix;

        // Handle font size scaling
        if (fontSize != BaseFontSize)
        {
            var scale = fontSize / (float)BaseFontSize;
            var scaleMatrix = Matrix4.CreateScale(scale, scale, 1);
            Matrix4.Mult(scaleMatrix, projectionMatrix, out matrix);
        }

        // Try to use cached geometry
        var cacheKey = $"{text}_{fontSize}";
        int vertexCount;
        float[] vertices;

        if (textCache.TryGetValue(cacheKey, out var cached))
        {
            // Use cached geometry, apply position offset
            cached.LastUsedFrame = currentFrame;
            vertices = ApplyPositionOffset(cached.Vertices, cached.OriginalX, cached.OriginalY, x, y);
            vertexCount = cached.VertexCount;
        }
        else
        {
            // Build new geometry
            vertexCount = BuildVertexBuffer(text, x, y);
            
            if (vertexCount == 0) return; // No valid characters to render

            vertices = new float[vertexCount];
            Array.Copy(vertexBuffer, vertices, vertexCount);

            // Cache the geometry if cache is not full
            if (textCache.Count < CacheMaxEntries)
            {
                textCache[cacheKey] = new CachedText
                {
                    Text = text,
                    FontSize = fontSize,
                    Vertices = vertices,
                    VertexCount = vertexCount,
                    LastUsedFrame = currentFrame,
                    OriginalX = x,
                    OriginalY = y
                };
            }
        }

        // Ensure VBO is large enough
        var requiredSize = vertexCount * sizeof(float);
        
        // Track peak usage
        if (requiredSize > peakVboUsage)
            peakVboUsage = requiredSize;
        
        // Grow buffer if needed
        if (requiredSize > currentVboSize)
        {
            ResizeVbo(requiredSize, grow: true);
        }
        // Shrink buffer if we've been consistently under-utilizing it
        else if (framesSinceLastResize > 300 && peakVboUsage < currentVboSize / 2)
        {
            // Only shrink if we've been using less than 50% for 300 frames (~5 seconds at 60 FPS)
            var targetSize = Math.Max(peakVboUsage * 2, MinBufferSize * sizeof(float));
            if (targetSize < currentVboSize)
            {
                ResizeVbo(targetSize, grow: false);
                peakVboUsage = requiredSize; // Reset peak tracking
            }
        }

        framesSinceLastResize++;

        // Save previous OpenGL states (do this once per Render call, not per character)
        var previousBlendEnabled = GL.IsEnabled(EnableCap.Blend);
        var previousBlendSrc = GL.GetInteger(GetPName.BlendSrc);
        var previousBlendDest = GL.GetInteger(GetPName.BlendDst);
        var previousDepthTestEnabled = GL.IsEnabled(EnableCap.DepthTest);

        // Enable blending & disable depth test
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Disable(EnableCap.DepthTest);

        // Bind the vertex array
        GL.BindVertexArray(vao);

        // Use the text shader program
        shader.Use();

        // Set the text color and projection uniforms
        shader.SetVector3("textColor", ref color);
        shader.SetMatrix4("projection", ref matrix);

        // Bind the font atlas texture
        fontAtlas.Texture.Use(TextureUnit.Texture31);

        // Upload ALL vertices at once
        GL.NamedBufferSubData(vbo, IntPtr.Zero, vertexCount * sizeof(float), vertices);
        
        // Single draw call for ALL characters!
        GL.DrawArrays(PrimitiveType.Triangles, 0, vertexCount / 4); // 4 floats per vertex (pos.xy + uv.xy)
        Log.CheckGlError();

        // Restore previous OpenGL states
        if (previousBlendEnabled)
            GL.Enable(EnableCap.Blend);
        else
            GL.Disable(EnableCap.Blend);
        GL.BlendFunc((BlendingFactor)previousBlendSrc, (BlendingFactor)previousBlendDest);

        if (previousDepthTestEnabled) 
            GL.Enable(EnableCap.DepthTest);

        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    /// <summary>
    /// Applies position offset to cached vertices.
    /// </summary>
    private float[] ApplyPositionOffset(float[] cachedVertices, float origX, float origY, float newX, float newY)
    {
        var offsetX = newX - origX;
        var offsetY = newY - origY;

        // If no offset needed, return original
        if (Math.Abs(offsetX) < 0.01f && Math.Abs(offsetY) < 0.01f)
            return cachedVertices;

        var result = new float[cachedVertices.Length];
        for (int i = 0; i < cachedVertices.Length; i += 4)
        {
            result[i] = cachedVertices[i] + offsetX;         // X position
            result[i + 1] = cachedVertices[i + 1] + offsetY; // Y position
            result[i + 2] = cachedVertices[i + 2];           // UV X
            result[i + 3] = cachedVertices[i + 3];           // UV Y
        }
        return result;
    }

    /// <summary>
    /// Evicts old cache entries using LRU strategy.
    /// </summary>
    private void EvictOldCacheEntries()
    {
        if (currentFrame % 60 != 0) return; // Only check every 60 frames (~1 second)

        var toRemove = textCache
            .Where(kvp => currentFrame - kvp.Value.LastUsedFrame > CacheEvictionFrames)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toRemove)
        {
            textCache.Remove(key);
        }

        if (toRemove.Count > 0)
        {
            Log.Debug($"TextRenderer cache evicted {toRemove.Count} entries (age > {CacheEvictionFrames} frames)");
        }
    }

    /// <summary>
    /// Resizes the VBO with smart growth/shrink strategy.
    /// </summary>
    private void ResizeVbo(int requiredSize, bool grow)
    {
        if (grow)
        {
            // Grow by factor or to required size, whichever is larger
            // Add extra headroom to avoid frequent resizes
            var targetSize = (int)Math.Max(requiredSize * 1.2f, currentVboSize * GrowthFactor);
            
            // Cap at maximum size for safety
            targetSize = Math.Min(targetSize, MaxBufferSize * sizeof(float));
            
            if (targetSize <= currentVboSize) return; // Already large enough
            
            currentVboSize = targetSize;
            GL.NamedBufferData(vbo, currentVboSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            
            Log.Debug($"TextRenderer VBO grown to {currentVboSize / 1024}KB ({currentVboSize / (24 * sizeof(float))} chars capacity)");
        }
        else
        {
            // Shrink to target size but maintain minimum
            var targetSize = Math.Max(requiredSize, MinBufferSize * sizeof(float));
            
            if (targetSize >= currentVboSize) return; // Not worth shrinking
            
            currentVboSize = targetSize;
            GL.NamedBufferData(vbo, currentVboSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            
            Log.Debug($"TextRenderer VBO shrunk to {currentVboSize / 1024}KB ({currentVboSize / (24 * sizeof(float))} chars capacity)");
        }
        
        Log.CheckGlError();
        framesSinceLastResize = 0;
    }

    /// <summary>
    /// Builds vertex buffer for all characters in the text string with kerning support.
    /// Returns the number of floats written to the buffer.
    /// </summary>
    private int BuildVertexBuffer(string text, float startX, float startY)
    {
        var dx = startX;
        var dy = startY;
        var vertexIndex = 0;

        // Ensure CPU-side buffer is large enough (6 vertices × 4 floats per character)
        var estimatedSize = text.Length * 24;
        if (vertexBuffer.Length < estimatedSize)
        {
            // Grow CPU buffer with same strategy
            var newSize = Math.Max(estimatedSize, (int)(vertexBuffer.Length * GrowthFactor));
            newSize = Math.Min(newSize, MaxBufferSize); // Cap at max size
            
            Array.Resize(ref vertexBuffer, newSize);
            
            Log.Debug($"TextRenderer CPU buffer resized to {newSize / 24} chars capacity");
        }

        for (int charIndex = 0; charIndex < text.Length; charIndex++)
        {
            var c = text[charIndex];
            
            if (c == '\n')
            {
                dy += fontAtlas.LineHeight;
                dx = startX;
                continue;
            }
            
            if (!fontAtlas.Glyphs.TryGetValue(c, out var glyph))
            {
                continue; // Skip characters not in atlas
            }

            // Add 6 vertices (2 triangles) for this character
            // Triangle 1: TL, BL, TR
            // Triangle 2: TR, BL, BR
            
            // Vertex 0: Top-left
            vertexBuffer[vertexIndex++] = dx;
            vertexBuffer[vertexIndex++] = dy;
            vertexBuffer[vertexIndex++] = glyph.UvMinX;
            vertexBuffer[vertexIndex++] = glyph.UvMinY;
            
            // Vertex 1: Bottom-left
            vertexBuffer[vertexIndex++] = dx;
            vertexBuffer[vertexIndex++] = dy + glyph.Height;
            vertexBuffer[vertexIndex++] = glyph.UvMinX;
            vertexBuffer[vertexIndex++] = glyph.UvMaxY;
            
            // Vertex 2: Top-right
            vertexBuffer[vertexIndex++] = dx + glyph.Width;
            vertexBuffer[vertexIndex++] = dy;
            vertexBuffer[vertexIndex++] = glyph.UvMaxX;
            vertexBuffer[vertexIndex++] = glyph.UvMinY;
            
            // Vertex 3: Top-right (duplicate for second triangle)
            vertexBuffer[vertexIndex++] = dx + glyph.Width;
            vertexBuffer[vertexIndex++] = dy;
            vertexBuffer[vertexIndex++] = glyph.UvMaxX;
            vertexBuffer[vertexIndex++] = glyph.UvMinY;
            
            // Vertex 4: Bottom-left (duplicate for second triangle)
            vertexBuffer[vertexIndex++] = dx;
            vertexBuffer[vertexIndex++] = dy + glyph.Height;
            vertexBuffer[vertexIndex++] = glyph.UvMinX;
            vertexBuffer[vertexIndex++] = glyph.UvMaxY;
            
            // Vertex 5: Bottom-right
            vertexBuffer[vertexIndex++] = dx + glyph.Width;
            vertexBuffer[vertexIndex++] = dy + glyph.Height;
            vertexBuffer[vertexIndex++] = glyph.UvMaxX;
            vertexBuffer[vertexIndex++] = glyph.UvMaxY;

            // Advance cursor by glyph width
            dx += glyph.Width;

            // Apply kerning if next character exists
            if (charIndex < text.Length - 1 && glyph.KerningPairs != null)
            {
                var nextChar = text[charIndex + 1];
                if (glyph.KerningPairs.TryGetValue(nextChar, out var kerning))
                {
                    dx += kerning;
                }
            }
        }

        return vertexIndex; // Total number of floats written
    }

    /// <summary>
    /// Gets current buffer statistics for debugging/monitoring.
    /// </summary>
    public (int vboSizeKB, int cpuBufferSizeKB, int charCapacity, int peakUsageKB) GetBufferStats()
    {
        return (
            vboSizeKB: currentVboSize / 1024,
            cpuBufferSizeKB: vertexBuffer.Length * sizeof(float) / 1024,
            charCapacity: currentVboSize / (24 * sizeof(float)),
            peakUsageKB: peakVboUsage / 1024
        );
    }

    /// <summary>
    /// Gets text cache statistics for monitoring.
    /// </summary>
    public (int cachedEntries, int maxEntries, long currentFrame) GetCacheStats()
    {
        return (
            cachedEntries: textCache.Count,
            maxEntries: CacheMaxEntries,
            currentFrame: currentFrame
        );
    }

    /// <summary>
    /// Clears the text cache. Useful when switching scenes or after large text changes.
    /// </summary>
    public void ClearCache()
    {
        textCache.Clear();
        Log.Debug("TextRenderer cache cleared");
    }
}
