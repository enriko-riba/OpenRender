using OpenRender.Core.Textures;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SixLabors.Fonts;
using SixLabors.Fonts.Unicode;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Drawing.Processing;
using System.Runtime.InteropServices;

namespace OpenRender.Text;

/// <summary>
/// Encapsulates a font atlas texture prerendered with glyphs from the given character set in given font and size.
/// </summary>
public sealed class FontAtlasGenerator
{
    const int Padding = 4;
    const int ImageSize = 256;

    public static IFontAtlas Create(string fontName, int fontSize, Color4 backgroundColor)
    {
        var chars = GenerateCharacters(32, 128);
        return Create(fontName, fontSize, chars, backgroundColor);
    }

    public static IFontAtlas Create(string fontName, int fontSize, ReadOnlySpan<char> characterSet, Color4 backgroundColor)
    {
        var fontCollection = new FontCollection();
        var family = fontCollection.Add(fontName);
        var font = family.CreateFont(fontSize);

        var textOptions = new TextOptions(font)
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var fontAtlas = new FontAtlas(textOptions);

        //  measure widest char and line height to calc image dimensions
        var charBounds = TextMeasurer.MeasureAdvance("W", textOptions);
        var rect = TextMeasurer.MeasureAdvance(characterSet, textOptions);

        var totalChars = characterSet.Length;
        var charWidth = (int)Math.Ceiling(charBounds.Width);
        var charHeight = (int)Math.Ceiling(rect.Height);
        var charsPerLine = (ImageSize - Padding * 2) / (charWidth + Padding);
        var totalLines = (int)Math.Ceiling(totalChars / (float)charsPerLine);
        var sizeW = charsPerLine * (charWidth + Padding) + Padding * 2;
        var sizeH = totalLines * (charHeight + Padding) + Padding * 2;
        fontAtlas.Size = new Vector2i(sizeW, sizeH);
        fontAtlas.CharWidth = charWidth;
        fontAtlas.LineHeight = charHeight;
        fontAtlas.CharacterFrameSize = new Vector2i(charWidth + Padding, charHeight + Padding);

        var image = new Image<Rgba32>(sizeW, sizeH);
        image.Mutate(ctx => ctx.BackgroundColor(backgroundColor.ToImageSharpColor()));

        var currentRow = 0;
        var currentY = (float)Padding;
        var counter = 0;
        var start = 0;
        foreach (var c in characterSet)
        {
            if (CodePoint.IsValid(c))
            {
                var row = ++counter / charsPerLine;
                var col = counter % charsPerLine;

                //  new row?
                if (currentRow != row)
                {
                    var slice = characterSet[start..counter];
                    AddRow(fontAtlas, font, textOptions, slice, image, ref currentY);
                    currentRow = row;
                    start = counter;
                }
            }
        }

        if (start < characterSet.Length)
        {
            var slice = characterSet[start..counter];
            AddRow(fontAtlas, font, textOptions, slice, image, ref currentY);
        }

        // TODO: for debug, remove
        image.SaveAsPng("font-atlas.png");

        var buffer = MemoryMarshal.AsBytes(image.GetPixelMemoryGroup().Single().Span).ToArray();
        fontAtlas.Texture = Texture.FromByteArray(buffer,
            sizeW,
            sizeH,
            "fontAtlasSampler",
            unit: TextureUnit.Texture16,
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
    private static void AddRow(FontAtlas fontAtlas, Font font, TextOptions style, ReadOnlySpan<char> text, Image<Rgba32> image, ref float textureY)
    {
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
    private static ReadOnlySpan<char> GenerateCharacters(int startAsciiCode, int endAsciiCode)
    {
        var arr = new char[endAsciiCode - startAsciiCode];
        for (var i = startAsciiCode; i < endAsciiCode; i++)
        {
            arr[i - startAsciiCode] = (char)i;
        }
        return arr;
    }
}