namespace OpenRender.Text;

/// <summary>
/// Glyph information including UV coordinates, dimensions, and kerning data.
/// Made struct for better memory efficiency (was class).
/// </summary>
public struct GlyphInfo
{
    public float UvMinX { get; set; }
    public float UvMaxX { get; set; }
    public float UvMinY { get; set; }
    public float UvMaxY { get; set; }

    /// <summary>
    /// Glyph width in pixel units.
    /// </summary>
    public float Width { get; set; }

    /// <summary>
    /// Glyphs height in pixel units.
    /// </summary>
    public float Height { get; set; }

    /// <summary>
    /// Kerning adjustments for character pairs.
    /// Key: next character, Value: horizontal offset adjustment
    /// </summary>
    public Dictionary<char, float>? KerningPairs { get; set; }

    public override string ToString() => $"x:{UvMinX}, y:{UvMinY}, x1:{UvMaxX}, y1:{UvMaxY}, w: {Width}, h: {Height}, kerning: {KerningPairs?.Count ?? 0}";
}
