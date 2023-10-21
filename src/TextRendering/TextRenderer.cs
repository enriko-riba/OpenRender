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
        GL.NamedBufferStorage(vbo, 24 * sizeof(float), 0, BufferStorageFlags.DynamicStorageBit);    //  this is to initialize the storage
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

    public static Matrix4 CreateTextRenderingProjection(float screenWidth, float screenHeight) => Matrix4.CreateOrthographicOffCenter(0, screenWidth, screenHeight, 0, -1, 1);

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
        var matrix = projectionMatrix;

        if (fontSize != BaseFontSize)
        {
            //  TODO: this is a hack to get the font sizes via scaling
            //  apply scaling
            var customOptions = new TextOptions(fontAtlas.TextOptions);
            var font = new Font(customOptions.Font, fontSize);
            customOptions.Font = font;
            var rectCustom = TextMeasurer.MeasureAdvance(text, customOptions);
            var rectBase = TextMeasurer.MeasureAdvance(text, fontAtlas.TextOptions);
            var scaleW = rectCustom.Width / rectBase.Width;
            var scaleH = rectCustom.Height / rectBase.Height;
            var scaleMatrix = Matrix4.CreateScale(scaleW, scaleH, 1);
            Matrix4.Mult(scaleMatrix, projectionMatrix, out matrix);
        }

        // Save previous OpenGL states
        var previousBlendEnabled = GL.IsEnabled(EnableCap.Blend);
        var previousBlendSrc = GL.GetInteger(GetPName.BlendSrc);
        var previousBlendDest = GL.GetInteger(GetPName.BlendDst);
        var previousDepthTestEnabled = GL.IsEnabled(EnableCap.DepthTest);

        // Enable blending & disable depth test
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Disable(EnableCap.DepthTest);

        // Bind the vertex array and draw all the characters in a single draw call
        GL.BindVertexArray(vao);

        // Use the text shader program
        shader.Use();

        // Set the text color and projection uniforms
        shader.SetVector3("textColor", ref color);
        shader.SetMatrix4("projection", ref matrix);

        // Bind the font atlas texture
        fontAtlas.Texture.Use(TextureUnit.Texture31);

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
            else if (!fontAtlas.Glyphs.ContainsKey(c))
            {
                continue;
            }
            var glyph = fontAtlas.Glyphs[c];
            var characterVertices = new float[]
            {
                    dx, dy,                                 glyph.UvMinX, glyph.UvMinY,
                    dx, dy + glyph.Height,                  glyph.UvMinX, glyph.UvMaxY,
                    dx + glyph.Width, dy,                   glyph.UvMaxX, glyph.UvMinY,
                    dx + glyph.Width, dy,                   glyph.UvMaxX, glyph.UvMinY,
                    dx, dy + glyph.Height,                  glyph.UvMinX, glyph.UvMaxY,
                    dx + glyph.Width, dy + glyph.Height,    glyph.UvMaxX, glyph.UvMaxY,
            };
            GL.NamedBufferSubData(vbo, 0, characterVertices.Length * sizeof(float), characterVertices);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
            Log.CheckGlError();
            dx += glyph.Width;
        }

        // Restore previous OpenGL states
        if (previousBlendEnabled)
            GL.Enable(EnableCap.Blend);
        else
            GL.Disable(EnableCap.Blend);
        GL.BlendFunc((BlendingFactor)previousBlendSrc, (BlendingFactor)previousBlendDest);

        if (previousDepthTestEnabled) GL.Enable(EnableCap.DepthTest);

        GL.BindTexture(TextureTarget.Texture2D, 0);
    }
}
