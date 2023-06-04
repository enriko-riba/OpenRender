using OpenRender.Core.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SixLabors.Fonts;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Drawing.Processing;
using System.Runtime.InteropServices;

namespace OpenRender;

public class TextRenderer : IDisposable
{
    private readonly Shader shader;
    private readonly Dictionary<char, Character> characters = new();
    private readonly int vao;
    private readonly int vbo;

    private Font? font;
    private TextOptions? textOptions;

    private struct Character
    {
        public int TextureID;
        public Vector2 Size;
        public Vector2 Bearing;
    }

    public TextRenderer()
    {
        shader = new Shader("Shaders/text.vert", "Shaders/text.frag");
        vao = GL.GenVertexArray();
        GL.BindVertexArray(vao);

        // Generate and bind the Vertex Buffer Object (VBO)
        vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);

        // Specify the vertex attribute pointers
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 2 * sizeof(float));
        GL.EnableVertexAttribArray(1);
    }

    public void LoadFont(string fontPath, float fontSize)
    {
        var fonts = new FontCollection();
        var font1 = fonts.Add(fontPath);
        font = font1.CreateFont(fontSize);
        textOptions = new TextOptions(font);
        characters.Clear();
    }

    public void RenderText(string text, float x, float y, Vector3 color, float screenWidth, float screenHeight)
        => RenderText(text, x, y, 1f, 1f, color, screenWidth, screenHeight);

    public void RenderText(string text, float x, float y, float scaleX, float scaleY, Vector3 color, float screenWidth, float screenHeight)
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

        var projectionMatrix = Matrix4.CreateOrthographicOffCenter(0, screenWidth, screenHeight, 0, -1, 1);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindVertexArray(vao);
        shader.Use();
        shader.SetMatrix4("projection", ref projectionMatrix);
        shader.SetVector3("textColor", ref color);

        foreach (var c in text)
        {
            if (!characters.ContainsKey(c))
                LoadCharacter(c);

            var character = characters[c];

            var xpos = x + character.Bearing.X * scaleX;
            var ypos = y - (character.Size.Y - character.Bearing.Y) * scaleY;
            var w = character.Size.X * scaleX;
            var h = character.Size.Y * scaleY;

            float[] vertices =
            {
                xpos,     ypos,     0.0f, 0.0f,
                xpos,     ypos + h, 0.0f, 1.0f,
                xpos + w, ypos,     1.0f, 0.0f,
                xpos + w, ypos,     1.0f, 0.0f,
                xpos,     ypos + h, 0.0f, 1.0f,
                xpos + w, ypos + h, 1.0f, 1.0f
            };

            GL.BindTexture(TextureTarget.Texture2D, character.TextureID);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo); // Bind the Vertex Buffer Object
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.DynamicDraw);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
            x += (int)Math.Ceiling(character.Size.X * scaleX);
        }

        // Restore previous OpenGL states
        if (previousBlendEnabled)
            GL.Enable(EnableCap.Blend);
        else
            GL.Disable(EnableCap.Blend);
        GL.BlendFunc((BlendingFactor)previousBlendSrc, (BlendingFactor)previousBlendDest);
        if (previousDepthTestEnabled)
            GL.Enable(EnableCap.DepthTest);
        else
            GL.Disable(EnableCap.DepthTest);
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    private void LoadCharacter(char c)
    {
        var size = TextMeasurer.Measure(c.ToString(), textOptions);
        var image = new Image<Rgba32>((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height));
        image.Mutate(ctx => ctx.DrawText(c.ToString(), font, Color.White, PointF.Empty));
        //image.SaveAsBmp($"dbg_glyph-{(byte)c}.bmp");
        var textureID = CreateTexture(image);
        var character = new Character()
        {
            TextureID = textureID,
            Size = new Vector2(image.Width, image.Height),
            Bearing = new Vector2(0, font.Size)
        };

        characters.Add(c, character);
    }

    private static int CreateTexture(Image<Rgba32> image)
    {
        var textureID = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, textureID);

        var imageBuffer = MemoryMarshal.AsBytes(image.GetPixelMemoryGroup().Single().Span).ToArray();
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, image.Width, image.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, imageBuffer);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        return textureID;
    }

    public void Dispose()
    {
        foreach (var character in characters.Values)
            GL.DeleteTexture(character.TextureID);
    }
}
