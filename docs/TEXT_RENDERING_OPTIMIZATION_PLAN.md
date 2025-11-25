# TextRendering Optimization Plan

## Phase 1: Batched Rendering (CRITICAL)

### Current Performance Issue
- **1 draw call per character** (100 chars = 100 draw calls)
- Loading screen: ~1,500 draw calls per frame
- CPU-bound, defeats GPU parallelism

### Proposed Solution: Batch All Characters

```csharp
public void Render(string text, int fontSize, float x, float y, Vector3 color)
{
    // Build vertex buffer for ALL characters at once
    var vertices = new List<float>(text.Length * 24); // 6 verts × 4 floats per char
    
    var dx = x;
    var dy = y;
    
    foreach (var c in text)
    {
        if (c == '\n')
        {
            dy += fontAtlas.LineHeight;
            dx = x;
            continue;
        }
        if (!fontAtlas.Glyphs.ContainsKey(c)) continue;
        
        var glyph = fontAtlas.Glyphs[c];
        
        // Add 6 vertices (2 triangles) for this character
        vertices.AddRange(new float[] {
            dx, dy,                                 glyph.UvMinX, glyph.UvMinY,
            dx, dy + glyph.Height,                  glyph.UvMinX, glyph.UvMaxY,
            dx + glyph.Width, dy,                   glyph.UvMaxX, glyph.UvMinY,
            dx + glyph.Width, dy,                   glyph.UvMaxX, glyph.UvMinY,
            dx, dy + glyph.Height,                  glyph.UvMinX, glyph.UvMaxY,
            dx + glyph.Width, dy + glyph.Height,    glyph.UvMaxX, glyph.UvMaxY,
        });
        
        dx += glyph.Width;
    }
    
    // Single buffer update
    GL.NamedBufferSubData(vbo, 0, vertices.Count * sizeof(float), vertices.ToArray());
    
    // Single draw call for ALL characters
    GL.DrawArrays(PrimitiveType.Triangles, 0, vertices.Count / 4);
}
```

**Expected Improvement:**
- Loading screen: 1,500 → ~10 draw calls (one per Render() call)
- **~150× reduction in draw calls**
- CPU usage drops significantly
- Frame time improvement: 5-10ms

### Dynamic Buffer Sizing

#### Implementation Overview
The TextRenderer now features intelligent dynamic buffer management with both growth and shrink capabilities to optimize memory usage while preventing frequent reallocations.

```csharp
// Constants
private const int InitialBufferSize = 4096;      // 4KB - enough for ~256 characters
private const float GrowthFactor = 1.5f;         // Grow by 50% when needed
private const int MinBufferSize = 1024;          // 1KB minimum (never shrink below)
private const int MaxBufferSize = 1048576;       // 1MB maximum (safety limit)

// Tracking variables
private int currentVboSize = InitialBufferSize * sizeof(float);
private int peakVboUsage = 0;                    // Track peak usage for shrink decisions
private int framesSinceLastResize = 0;           // Prevent frequent resize operations
private float[] vertexBuffer = new float[InitialBufferSize];
```

#### Growth Strategy

**When to Grow:**
- Buffer grows automatically when required size exceeds current capacity
- Growth includes 20% headroom to avoid immediate reallocation: `requiredSize * 1.2f`
- Minimum growth is 1.5× current size (whichever is larger)
- Capped at 1MB maximum for safety

**Example Growth Sequence:**
```
Initial: 4KB (256 chars)
→ Need 350 chars: Grow to 6KB (375 chars)
→ Need 600 chars: Grow to 9KB (562 chars)
→ Need 1000 chars: Grow to 13.5KB (843 chars)
```

**Code:**
```csharp
if (requiredSize > currentVboSize)
{
    // Grow by factor or to required size + headroom, whichever is larger
    var targetSize = (int)Math.Max(requiredSize * 1.2f, currentVboSize * GrowthFactor);
    targetSize = Math.Min(targetSize, MaxBufferSize * sizeof(float));
    
    currentVboSize = targetSize;
    GL.NamedBufferData(vbo, currentVboSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
    
    Log.Debug($"VBO grown to {currentVboSize / 1024}KB");
    framesSinceLastResize = 0;
}
```

#### Shrink Strategy

**When to Shrink:**
- After 300 frames (~5 seconds at 60 FPS) of consistent under-utilization
- Only shrinks if peak usage < 50% of current buffer size
- Target size = peak usage × 2 (maintains 2× headroom)
- Never shrinks below 1KB minimum

**Benefits:**
- Prevents memory waste after rendering large text blocks
- Automatic memory recovery in loading → game transitions
- Avoids thrashing with hysteresis (2× headroom prevents immediate re-growth)

**Code:**
```csharp
else if (framesSinceLastResize > 300 && peakVboUsage < currentVboSize / 2)
{
    var targetSize = Math.Max(peakVboUsage * 2, MinBufferSize * sizeof(float));
    if (targetSize < currentVboSize)
    {
        currentVboSize = targetSize;
        GL.NamedBufferData(vbo, currentVboSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        peakVboUsage = requiredSize; // Reset peak tracking
        framesSinceLastResize = 0;
    }
}
```

#### Dual Buffer Management

Both GPU (VBO) and CPU (vertex array) buffers resize independently:

**VBO (GPU):**
- Resizes via `GL.NamedBufferData()`
- Size tracked in bytes: `currentVboSize`
- Logs resize operations

**CPU Array:**
- Resizes via `Array.Resize()`
- Size tracked in float count: `vertexBuffer.Length`
- Uses same growth factor strategy

#### Performance Characteristics

| Scenario | Initial | After Growth | After Shrink | Memory Saved |
|----------|---------|--------------|--------------|--------------|
| Small UI (50 chars) | 4KB | 4KB | 4KB | 0KB |
| Loading screen (500 chars) | 4KB | 13.5KB | 13.5KB | 0KB |
| Post-loading (50 chars) | 13.5KB | 13.5KB | 4KB (after 5s) | 9.5KB |
| Burst to 1000 chars | 13.5KB | 27KB | 27KB | 0KB |

#### Monitoring API

Get current buffer statistics for debugging:

```csharp
var (vboKB, cpuKB, capacity, peakKB) = textRenderer.GetBufferStats();
Console.WriteLine($"VBO: {vboKB}KB, CPU: {cpuKB}KB, Capacity: {capacity} chars, Peak: {peakKB}KB");
```

**Example Output:**
```
VBO: 13KB, CPU: 16KB, Capacity: 843 chars, Peak: 11KB
```

#### Edge Cases Handled

1. **Empty String**: Returns immediately, no allocation
2. **Very Large Text (>1MB)**: Capped at MaxBufferSize, logs warning
3. **Rapid Size Changes**: Hysteresis prevents thrashing (300 frame delay + 2× headroom)
4. **Memory Pressure**: Automatic shrinking reclaims unused memory
5. **CPU-GPU Sync**: Both buffers independently managed but coordinated

#### Performance Impact

**Before (no dynamic sizing):**
- Fixed 24-byte buffer: constant reallocation OR huge waste
- No shrinking: loading screen buffers persist forever

**After (enhanced dynamic sizing):**
- Automatic growth: 0 reallocations for typical usage
- Automatic shrinking: memory recovered after 5 seconds
- Peak tracking: optimal size without manual tuning

#### Best Practices

1. **Don't pre-allocate**: Let the system grow naturally
2. **Monitor in debug**: Use `GetBufferStats()` to verify behavior
3. **Trust the hysteresis**: 300-frame delay prevents thrashing
4. **Expect brief spikes**: First large text will trigger resize

#### Future Enhancements

1. **Adaptive growth factor**: Learn from usage patterns
2. **LRU-based shrinking**: Shrink based on access patterns, not time
3. **Buffer pooling**: Share buffers across multiple TextRenderer instances
4. **Texture atlas resizing**: Dynamic font atlas similar to vertex buffer

---

## Phase 2: State Management (Est. 2-3 hours)

### Problem: Redundant State Queries
```csharp
// Current: Called every Render()
var previousBlendEnabled = GL.IsEnabled(EnableCap.Blend);  // Driver query (slow!)
var previousBlendSrc = GL.GetInteger(GetPName.BlendSrc);
var previousBlendDest = GL.GetInteger(GetPName.BlendDst);
var previousDepthTestEnabled = GL.IsEnabled(EnableCap.DepthTest);
```

### Solution: Cached State Manager
```csharp
private struct RenderState
{
    public bool BlendEnabled;
    public int BlendSrc;
    public int BlendDst;
    public bool DepthTestEnabled;
}

private RenderState? savedState;

private void SaveState()
{
    savedState = new RenderState
    {
        BlendEnabled = GL.IsEnabled(EnableCap.Blend),
        BlendSrc = GL.GetInteger(GetPName.BlendSrc),
        BlendDst = GL.GetInteger(GetPName.BlendDst),
        DepthTestEnabled = GL.IsEnabled(EnableCap.DepthTest)
    };
}

private void RestoreState()
{
    if (savedState.HasValue)
    {
        var state = savedState.Value;
        if (state.BlendEnabled) GL.Enable(EnableCap.Blend);
        else GL.Disable(EnableCap.Blend);
        GL.BlendFunc((BlendingFactor)state.BlendSrc, (BlendingFactor)state.BlendDst);
        if (state.DepthTestEnabled) GL.Enable(EnableCap.DepthTest);
    }
}
```

**Better:** Only save state once per frame, not per Render() call

---

## Phase 3: Text Caching (Est. 4-6 hours)

### Concept
Cache frequently rendered text (like "FPS: 150") to avoid rebuilding geometry

```csharp
private class CachedText
{
    public string Text;
    public int FontSize;
    public float[] Vertices;
    public int VertexCount;
    public long LastUsedFrame;
}

private Dictionary<string, CachedText> textCache = new();
private long currentFrame = 0;

public void Render(string text, int fontSize, float x, float y, Vector3 color)
{
    currentFrame++;
    
    var cacheKey = $"{text}_{fontSize}";
    
    if (!textCache.TryGetValue(cacheKey, out var cached))
    {
        // Build geometry (as in Phase 1)
        cached = new CachedText { 
            Text = text, 
            FontSize = fontSize, 
            Vertices = BuildVertices(text, fontSize, 0, 0),
            VertexCount = /* ... */
        };
        textCache[cacheKey] = cached;
    }
    
    cached.LastUsedFrame = currentFrame;
    
    // Apply position offset (translate cached vertices)
    var translatedVerts = ApplyOffset(cached.Vertices, x, y);
    
    // Render cached geometry
    GL.NamedBufferSubData(vbo, 0, translatedVerts.Length * sizeof(float), translatedVerts);
    GL.DrawArrays(PrimitiveType.Triangles, 0, cached.VertexCount);
}

// Periodically clean old cache entries
private void CleanCache()
{
    if (currentFrame % 300 == 0) // Every ~5 seconds at 60 FPS
    {
        var toRemove = textCache.Where(kvp => currentFrame - kvp.Value.LastUsedFrame > 180).ToList();
        foreach (var kvp in toRemove)
            textCache.Remove(kvp.Key);
    }
}
```

**Expected Improvement:**
- Static UI text (labels, FPS counter): ~3× faster
- Dynamic text still benefits from batching

---

## Phase 4: Font Size Optimization (Est. 3-4 hours)

### Option A: Pre-generate Common Sizes
```csharp
public class FontAtlasSet
{
    private Dictionary<int, IFontAtlas> atlases = new();
    
    public FontAtlasSet(string fontPath, params int[] sizes)
    {
        foreach (var size in sizes)
        {
            atlases[size] = FontAtlasGenerator.Create(fontPath, size, Color4.Transparent);
        }
    }
    
    public IFontAtlas GetAtlas(int requestedSize)
    {
        // Return exact match or closest size
        return atlases.OrderBy(kvp => Math.Abs(kvp.Key - requestedSize)).First().Value;
    }
}
```

### Option B: Accept Scaling Artifacts
Remove the expensive scaling calculation, just scale the projection matrix:
```csharp
// Much simpler, faster
if (fontSize != BaseFontSize)
{
    var scale = fontSize / (float)BaseFontSize;
    matrix = Matrix4.CreateScale(scale, scale, 1) * projectionMatrix;
}
```

**Trade-off:** Slight blur at non-native sizes vs. 10× faster rendering

---

## Phase 5: Kerning Support (Est. 2-3 hours)

```csharp
// In AddRow():
var kerning = font.GetKerning(text[j], text[j+1]);  // SixLabors.Fonts supports this
gi.Kerning = new Dictionary<char, float>();
gi.Kerning[text[j+1]] = kerning;

// In Render():
if (glyph.Kerning?.TryGetValue(nextChar, out var kern) == true)
    dx += kern;
```

---

## Performance Targets

| Metric | Before | After Phase 1 | After All Phases |
|--------|--------|---------------|------------------|
| Draw calls (loading screen) | ~1,500 | ~10 | ~10 |
| Frame time (text rendering) | 8-12ms | 0.5-1ms | 0.2-0.5ms |
| Memory allocations/frame | High | Medium | Low (cached) |

---

## Implementation Priority

1. **Phase 1 (Batching)** - Critical, implement first
2. **Phase 2 (State)** - Quick win, low effort
3. **Phase 4B (Scaling)** - Quick fix for font size issue
4. **Phase 3 (Caching)** - Optional, if text rendering is still a bottleneck
5. **Phase 5 (Kerning)** - Polish, not performance-critical

---

## Additional Recommendations

### Memory Management
- Make `GlyphInfo` a `struct` instead of `class`
- Implement `IDisposable` for TextRenderer (dispose VAO, VBO, shader)
- Share shader instances across multiple TextRenderer objects

### API Improvements
```csharp
// Add overloads for common cases
public void RenderCentered(string text, int fontSize, float centerX, float y, Vector3 color);
public void RenderRight(string text, int fontSize, float rightX, float y, Vector3 color);

// Add color with alpha
public void Render(string text, int fontSize, float x, float y, Vector4 color);
```

### Shader Upgrades
- Upgrade from OpenGL 3.3 → 4.6
- Use instanced rendering for even better batching
- Add outline/shadow effects

### Unicode Support
```csharp
public static IFontAtlas CreateExtended(string fontName, int fontSize, UnicodeRange[] ranges)
{
    // Support Latin Extended, Cyrillic, Greek, etc.
}
