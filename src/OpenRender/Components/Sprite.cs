using OpenRender.Core;
using OpenRender.Core.Geometry;
using OpenRender.Core.Rendering;
using OpenRender.Core.Textures;
using OpenRender.SceneManagement;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;

namespace OpenRender.Components;

public class Sprite : SceneNode
{
    private Matrix4 projection;
    private Vector3 tint;
    private float angleRotation;
    private Vector2 pivot;

    protected Shader shader;

    public Sprite(string textureName) : base(default, default)
    {
        ArgumentNullException.ThrowIfNull(textureName);

        projection = Matrix4.CreateOrthographicOffCenter(0, 800, 600, 0, -1, 1);
        shader = new Shader("Shaders/sprite.vert", "Shaders/sprite.frag");
        shader.SetMatrix4("projection", ref projection);
        Material = Material.Create(shader,
            new TextureDescriptor[] { new TextureDescriptor(textureName, TextureType: TextureType.Diffuse, GenerateMipMap: false) }
        );

        TextureWidth = Material.Textures![0].Width;
        TextureHeight = Material.Textures[0].Height;

        var vbQuad = GeometryHelper.Create2dQuad();
        var mesh = new Mesh(vbQuad, DrawMode.Indexed);
        SetMesh(ref mesh);
        Tint = Color4.White;
        Pivot = new Vector2(0.5f, 0.5f);
        DisableCulling = true;
        RenderGroup = RenderGroup.UI;
    }

    public int TextureWidth { get; protected set; }
    public int TextureHeight { get; protected set; }

    /// <summary>
    /// Sprite tint. 
    /// The tint is actually a color filter where the texture color components 
    /// are multiplied with the tint components. The alpha value is ignored.
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
        shader.SetMatrix4("projection", ref projection);
    }

    /// <summary>
    /// Sprite rotation in degrees.
    /// </summary>
    public new float AngleRotation
    {
        get => angleRotation;
        set
        {
            angleRotation = value;
            SetRotation(new Vector3(0, 0, MathHelper.DegreesToRadians(angleRotation)));
        }
    }

    /// <summary>
    /// The pivot point (center of rotation) of the sprite.
    /// </summary>
    public Vector2 Pivot
    {
        get => pivot;
        set
        {
            if (value.X < 0 || value.X > 1 || value.Y < 0 || value.Y > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(Pivot), "Pivot must be between 0 and 1");
            }
            pivot = value;
        }
    }

    /// <summary>
    /// Calculates the world matrix (SROT).
    /// </summary>
    protected override void UpdateMatrix()
    {
        //  calculate scale, account for quad vertices in range [0,1] so we need to multiply with texture size
        var spriteScale = new Vector3(scale.X * TextureWidth, scale.Y * TextureHeight, 1);
        Matrix4.CreateScale(spriteScale, out scaleMatrix);

        // Calculate translation so that rotation is around the sprite's pivot       
        var spriteCenterOffset = new Vector3(spriteScale.X * Pivot.X, spriteScale.Y * Pivot.Y, 0);
        Matrix4.CreateTranslation(-spriteCenterOffset, out var offsetTranslationMatrix);

        //  now that the translation moves the sprite to origin, we can rotate it
        Matrix4.CreateFromQuaternion(rotation, out rotationMatrix);
        Matrix4.Mult(offsetTranslationMatrix, rotationMatrix, out var originRotationMatrix);

        Matrix4.Mult(scaleMatrix, originRotationMatrix, out worldMatrix);
        Matrix4.CreateTranslation(position + spriteCenterOffset, out var translationMatrix);
        Matrix4.Mult(worldMatrix, translationMatrix, out worldMatrix);

        if (Parent is not null and Sprite)
        {
            Parent.GetWorldMatrix(out var parentWorldMatrix);
            var parentWorldMatrixWithoutScale = new Matrix4(
                 parentWorldMatrix.Row0 / parentWorldMatrix.Row0.Length,
                 parentWorldMatrix.Row1 / parentWorldMatrix.Row1.Length,
                 parentWorldMatrix.Row2 / parentWorldMatrix.Row2.Length,
                 parentWorldMatrix.Row3
             );
            //  TODO: if we want to include parent scale, we need to multiply with a matrix created from parents scale.
            //        Can't use the parents scale matrix directly since it includes the texture dimensions.
            Matrix4.Mult(worldMatrix, parentWorldMatrixWithoutScale, out worldMatrix);
        }
    }

    public override void OnDraw(Scene scene, double elapsed)
    {
        var previousDepthTestEnabled = GL.IsEnabled(EnableCap.DepthTest);
        GL.Disable(EnableCap.DepthTest);

        base.OnDraw(scene, elapsed);

        if (previousDepthTestEnabled) GL.Enable(EnableCap.DepthTest);
    }
}
