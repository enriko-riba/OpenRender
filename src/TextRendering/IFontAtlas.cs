using OpenRender.Core.Textures;

namespace OpenRender.Text;

public interface IFontAtlas
{
    Dictionary<char, GlyphInfo> Glyphs { get; }
    Texture Texture { get; }
    int LineHeight { get; }
}
