namespace OpenRender.Text;

public class GlyphInfo
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

    public override string ToString() => $"x:{UvMinX}, y:{UvMinY}, x1:{UvMaxX}, y1:{UvMaxY}, w: {Width}, h: {Height}";
}
