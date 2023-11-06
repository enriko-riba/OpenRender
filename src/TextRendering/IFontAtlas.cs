using OpenRender.Core.Textures;
using SixLabors.Fonts;

namespace OpenRender.Text;

public interface IFontAtlas
{
    Dictionary<char, GlyphInfo> Glyphs { get; }
    TextureBase Texture { get; }
    int LineHeight { get; }
    TextOptions TextOptions { get; }
}
