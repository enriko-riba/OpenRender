using OpenRender.Core.Textures;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SixLabors.Fonts;
using SixLabors.Fonts.Unicode;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Drawing.Processing;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenRender.Text;

/// <summary>
/// Encapsulates a font atlas texture prerendered with glyphs from the given character set in given font and size.
/// </summary>
public class FontAtlas
{
    const int Padding = 4;
    const int ImageSize = 256;

    public Texture Texture { get; private set; } = default!;

    public Dictionary<char, GlyphInfo> Glyphs { get; } = new Dictionary<char, GlyphInfo>();

    /// <summary>
    /// Texture size.
    /// </summary>
    public Vector2i Size { get; private set; }

    public int CharWidth { get; private set; }

    public Vector2i CharacterFrameSize { get; private set; }

    public static FontAtlas Create(string fontName, int fontSize, Color4 backgroundColor)
    {
        var fontCollection = new FontCollection();
        var family = fontCollection.Add(fontName);
        var font = family.CreateFont(fontSize);

        var textOptions = new TextOptions(font)
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var fontAtlas = new FontAtlas();
        var sb = new StringBuilder();

        //  measure widest char and line height to calc image dimensions
        var charBounds = TextMeasurer.MeasureAdvance("W", textOptions);
        var chars = GenerateCharacters(32, 128);
        var rect = TextMeasurer.MeasureAdvance(chars, textOptions);

        var totalChars = 128 - 32;
        var charWidth = (int)Math.Ceiling(charBounds.Width);
        var charHeight = (int)Math.Ceiling(rect.Height);
        var charsPerLine = (ImageSize - Padding * 2) / (charWidth + Padding);
        var totalLines = (int)Math.Ceiling(totalChars / (float)charsPerLine);
        var sizeW = charsPerLine * (charWidth + Padding) + Padding * 2;
        var sizeH = totalLines * (charHeight + Padding) + Padding * 2;
        fontAtlas.Size = new Vector2i(sizeW, sizeH);
        fontAtlas.CharWidth = charWidth;
        fontAtlas.CharacterFrameSize = new Vector2i(charWidth + Padding, charHeight + Padding);

        var image = new Image<Rgba32>(sizeW, sizeH);
        image.Mutate(ctx => ctx.BackgroundColor(backgroundColor.ToImageSharpColor()));

        var currentRow = 0;
        var currentY = (float)Padding;
        var counter = 0;
        for (var i = 32; i < 128; i++)
        {
            if (CodePoint.IsValid(i))
            {
                var row = counter / charsPerLine;
                var col = counter % charsPerLine;
                counter++;

                //  new row?
                if (currentRow != row)
                {
                    AddRow(fontAtlas, font, textOptions, sb, image, ref currentY);
                    currentRow = row;
                }
                sb.Append((char)i);
            }
        }

        if (sb.Length > 0)
        {
            AddRow(fontAtlas, font, textOptions, sb, image, ref currentY);
        }

        // TODO: for debug, remove
        image.SaveAsPng("atlas.png");
        var buffer = MemoryMarshal.AsBytes(image.GetPixelMemoryGroup().Single().Span).ToArray();
        fontAtlas.Texture = Texture.FromByteArray(buffer, sizeW, sizeH, "fontAtlasSampler", TextureUnit.Texture16,
            minFilter: TextureMinFilter.Nearest, magFilter: TextureMagFilter.Nearest);

        return fontAtlas;
    }

    /// <summary>
    /// Add a row of characters to the texture.
    /// </summary>
    /// <param name="fontAtlas"></param>
    /// <param name="font"></param>
    /// <param name="style"></param>
    /// <param name="sb"></param>
    /// <param name="image"></param>
    /// <param name="textureY"></param>
    private static void AddRow(FontAtlas fontAtlas, Font font, TextOptions style, StringBuilder sb, Image<Rgba32> image, ref float textureY)
    {
        var text = sb.ToString();
        sb.Clear();
        var textureX = (float)Padding;

        TextMeasurer.TryMeasureCharacterAdvances(text, style, out var glyphBounds);
        var drawY = textureY + fontAtlas.CharacterFrameSize.Y;
        var rto = new RichTextOptions(font)
        {
            ColorFontSupport = ColorFontSupport.None,
            HorizontalAlignment = style.HorizontalAlignment,
            VerticalAlignment = style.VerticalAlignment,
            Origin = new PointF(textureX, drawY),
        };
        var brush = new SolidBrush(Color.White);

        for (var j = 0; j < text.Length; j++)
        {
            var c = text[j].ToString();
            image.Mutate(ctx => ctx.DrawText(rto, c, brush));

            var rect = TextMeasurer.MeasureAdvance(c, style);
            var uvFrameFactorX = rect.Width / fontAtlas.Size.X;
            var uvFrameFactorY = rect.Height / fontAtlas.Size.Y;
            var uvMinX = textureX / fontAtlas.Size.X;
            var uvMinY = (textureY + Padding) / fontAtlas.Size.Y;
            var uvMaxX = uvMinX + uvFrameFactorX;
            var uvMaxY = uvMinY + uvFrameFactorY;

            var gi = new GlyphInfo()
            {
                Width = rect.Width,
                Height = rect.Height,
                UvMinX = uvMinX,
                UvMinY = uvMinY,
                UvMaxX = uvMaxX,
                UvMaxY = uvMaxY,
            };
            textureX += fontAtlas.CharWidth + Padding;
            rto.Origin = new PointF(textureX, drawY);
            fontAtlas.Glyphs.Add(text[j], gi);
        }
        textureY += fontAtlas.CharacterFrameSize.Y;
    }

    /// <summary>
    /// Generates a string of characters from startAsciiCode to endAsciiCode.
    /// </summary>
    /// <param name="startAsciiCode">included starting ASCII character code</param>
    /// <param name="endAsciiCode">excluded ending ASCII character code</param>
    /// <returns></returns>
    private static string GenerateCharacters(int startAsciiCode, int endAsciiCode)
    {
        var sb = new StringBuilder();
        for (var i = startAsciiCode; i < endAsciiCode; i++)
        {
            sb.Append((char)i);
        }
        return sb.ToString();
    }
}