using OpenRender.Core;
using OpenRender.Core.Geometry;
using OpenRender.Core.Rendering;
using OpenRender.Core.Textures;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;

namespace OpenRender.Components;

public class Sprite : SceneNode
{
    private readonly Shader shader;
    private Matrix4 projection;
    private Vector3 tint;

    public Sprite(string textureName) : base(default, default)
    {
        ArgumentNullException.ThrowIfNull(textureName);

        projection = Matrix4.CreateOrthographicOffCenter(0, 800, 600, 0, -1, 1);
        shader = new Shader("Shaders/sprite.vert", "Shaders/sprite.frag");
        shader.SetMatrix4("projection", ref projection);
        Material = Material.Create(shader,
            new TextureDescriptor[] { new TextureDescriptor(textureName, TextureType: TextureType.Diffuse) }
        );

        var w = Material.Textures![0].Width;
        var h = Material.Textures[0].Height;

        var vbQuad = GeometryHelper.Create2dQuad(w, h);
        var mesh = new Mesh(vbQuad, DrawMode.Indexed);
        SetMesh(ref mesh);
        Tint = Color4.White;
        DisableCulling = true;
        RenderGroup = RenderGroup.UI;
    }

    /// <summary>
    /// Sprite tint. Note that the tint is actually a color filter where the texture color components are multiplied with the tint components. The alpha value is ignored.
    /// </summary>
    public Color4 Tint
    {
        get => new(tint.X, tint.Y, tint.Z, 1);
        set
        {
            tint = new(value.R, value.G, value.B);
            shader.SetVector3("tint", ref tint);
        }
    }

    /// <inheritdoc />
    public override void OnResize(Scene scene, ResizeEventArgs e)
    {
        projection = Matrix4.CreateOrthographicOffCenter(0, e.Width, e.Height, 0, -1, 1);
    }

    /// <inheritdoc />
    public override void OnDraw(Scene scene, double elapsed)
    {
        shader.SetMatrix4("projection", ref projection);
        base.OnDraw(scene, elapsed);
    }
}
