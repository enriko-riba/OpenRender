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
    private Matrix4 projectionMatrix;

    public TextRenderer(Matrix4 projection, IFontAtlas fontAtlas)
    {
        projectionMatrix = projection;
        this.fontAtlas = fontAtlas;

        vao = GL.GenVertexArray();
        vbo = GL.GenBuffer();

        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
        GL.VertexAttribPointer(3, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 2 * sizeof(float));
        GL.EnableVertexAttribArray(0);
        GL.EnableVertexAttribArray(3);
        shader = new Shader("Shaders/text.vert", "Shaders/text.frag");

        // Set the font atlas texture as a uniform in the shader
        shader.Use();
        GL.Uniform1(shader.GetUniformLocation("fontAtlasSampler"), (int)fontAtlas.Texture.TextureUnit - (int)TextureUnit.Texture0);
    }

    public IFontAtlas FontAtlas => fontAtlas;

    public Matrix4 Projection { get => projectionMatrix; set { projectionMatrix = value; } }

    public static Matrix4 CreateTextRenderingProjection(float screenWidth, float screenHeight) => Matrix4.CreateOrthographicOffCenter(0, screenWidth, screenHeight, 0, -1, 1);

    public Core.Rectangle Measure(string text)
    {
        var rect = TextMeasurer.MeasureAdvance(text, fontAtlas.TextOptions);
        return new Core.Rectangle(0, 0, (int)Math.Ceiling(rect.Width), (int)Math.Ceiling(rect.Height));
    }

    public void Render(string text, float x, float y, Vector3 color)
    {
        // Save previous OpenGL states
        var previousBlendEnabled = GL.IsEnabled(EnableCap.Blend);
        var previousBlendSrc = GL.GetInteger(GetPName.BlendSrc);
        var previousBlendDest = GL.GetInteger(GetPName.BlendDst);
        var previousDepthTestEnabled = GL.IsEnabled(EnableCap.DepthTest);

        // Enable blending & disable depth test
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Disable(EnableCap.DepthTest);

        // Use the text shader program
        shader.Use();

        // Set the text color and projection uniforms
        shader.SetVector3("textColor", ref color);
        shader.SetMatrix4("projection", ref projectionMatrix);

        // Bind the font atlas texture
        fontAtlas.Texture.Use();

        // Bind the vertex array and draw all the characters in a single draw call
        GL.BindVertexArray(vao);
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
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, characterVertices.Length * sizeof(float), characterVertices, BufferUsageHint.StaticDraw);
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
        GL.BindVertexArray(0);
    }
}
