using OpenRender.Core.Textures;
using OpenTK.Mathematics;
using SixLabors.Fonts;

namespace OpenRender.Text;

internal class FontAtlas : IFontAtlas
{
    public FontAtlas(TextOptions textOptions)
    {
        TextOptions = textOptions;
    }

    public TextureBase Texture { get; internal set; } = default!;

    public Dictionary<char, GlyphInfo> Glyphs { get; } = new Dictionary<char, GlyphInfo>();

    /// <summary>
    /// Texture size.
    /// </summary>
    public Vector2i Size { get; internal set; }

    public int CharWidth { get; internal set; }

    public Vector2i CharacterFrameSize { get; internal set; }

    public int LineHeight { get; internal set; }

    public TextOptions TextOptions { get; }
}
