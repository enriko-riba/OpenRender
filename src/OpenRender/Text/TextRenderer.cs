using OpenRender.Core.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SixLabors.Fonts;

namespace OpenRender.Text;

public class TextRenderer
{
    private readonly int vao;
    private readonly int vbo;
    private readonly Shader shader;
    private readonly FontAtlas fontAtlas;
    private Matrix4 projectionMatrix;

    public TextRenderer(string fontName, int size, Matrix4 projection)
    {
        projectionMatrix = projection;

        var fontCollection = new FontCollection();
        var family = fontCollection.Add(fontName);
        var font = family.CreateFont(size);
        //font = SystemFonts.CreateFont("Arial", size, FontStyle.Regular);
        var backgroundColor = new Color4(0f, 0f, 0f, 0.20f);
        fontAtlas = FontAtlas.Create(font, backgroundColor);

        vao = GL.GenVertexArray();
        vbo = GL.GenBuffer();

        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 2 * sizeof(float));
        GL.EnableVertexAttribArray(0);
        GL.EnableVertexAttribArray(1);
        shader = new Shader("Shaders/text.vert", "Shaders/text.frag");

        // Set the font atlas texture as a uniform in the shader
        shader.Use();
        GL.Uniform1(shader.GetUniformLocation("fontAtlasSampler"), (int)fontAtlas.Texture.TextureUnit - (int)TextureUnit.Texture0);
    }

    public Matrix4 Projection { get => projectionMatrix; set { projectionMatrix = value; } }

    public static Matrix4 CreateTextRenderingProjection(float screenWidth, float screenHeight) => Matrix4.CreateOrthographicOffCenter(0, screenWidth, screenHeight, 0, -1, 1);

    public void Render(string text, float x, float y, Vector3 color)
    {
        // Use the text shader program
        shader.Use();

        // Set the text color and projection uniforms
        shader.SetVector3("textColor", ref color);
        shader.SetMatrix4("projection", ref projectionMatrix);

        // Bind the font atlas texture
        fontAtlas.Texture.Use();

        // Bind the vertex array and draw all the characters in a single draw call
        GL.BindVertexArray(vao);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is >= (char)32 and <= (char)126) // ASCII characters from 32 to 126
            {
                var glyph = fontAtlas.Glyphs[c];
                var characterVertices = new float[]
                {
                    x, y,                                 glyph.UvMinX, glyph.UvMinY,
                    x, y + glyph.Height,                  glyph.UvMinX, glyph.UvMaxY,
                    x + glyph.Width, y,                   glyph.UvMaxX, glyph.UvMinY,
                    x + glyph.Width, y,                   glyph.UvMaxX, glyph.UvMinY,
                    x, y + glyph.Height,                  glyph.UvMinX, glyph.UvMaxY,
                    x + glyph.Width, y + glyph.Height,    glyph.UvMaxX, glyph.UvMaxY,
                };
                GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
                GL.BufferData(BufferTarget.ArrayBuffer, characterVertices.Length * sizeof(float), characterVertices, BufferUsageHint.StaticDraw);
                GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
                Log.CheckGlError();
                x += glyph.Width;
            }
        }

        GL.BindVertexArray(0);
    }
}
