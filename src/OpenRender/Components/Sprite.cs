﻿using OpenRender.Core;
using OpenRender.Core.Geometry;
using OpenRender.Core.Rendering;
using OpenRender.Core.Textures;
using OpenRender.SceneManagement;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;

namespace OpenRender.Components;

/// <summary>
/// 2D sprite component, renders a textured rectangle ignoring depth.
/// </summary>
public class Sprite : SceneNode
{
    private Vector3 tint;
    private float angleRotation;
    private Vector2 pivot;

    protected Shader shader;
    protected Matrix4 projection;

    /// <summary>
    /// The texture dimensions.
    /// </summary>
    protected Vector2i size;

    /// <summary>
    /// A frame inside the sprite texture that will be rendered, the default is the whole texture
    /// </summary>
    private Rectangle sourceRectangle;

    /// <summary>
    /// Creates a new 2D sprite object
    /// </summary>
    /// <param name="textureName"></param>
    public Sprite(string textureName) : base(default, default)
    {
        ArgumentNullException.ThrowIfNull(textureName);

        projection = Matrix4.CreateOrthographicOffCenter(0, 0, 0, 0, -1, 1);
        shader = new Shader("Shaders/sprite.vert", "Shaders/sprite.frag");
        shader.SetMatrix4("projection", ref projection);
        Material = Material.Create(shader,
            new TextureDescriptor[] {
                new TextureDescriptor(textureName,
                    TextureType: TextureType.Diffuse,
                    MagFilter: TextureMagFilter.Nearest,
                    MinFilter: TextureMinFilter.LinearMipmapLinear,
                    TextureWrapS: TextureWrapMode.ClampToBorder,
                    TextureWrapT: TextureWrapMode.ClampToBorder,
                    GenerateMipMap: true)
            }
        );

        size.X = Material.Textures![0].Width;
        size.Y = Material.Textures[0].Height;
        sourceRectangle.Width = size.X;
        sourceRectangle.Height = size.Y;

        var vbQuad = GeometryHelper.Create2dQuad();
        var mesh = new Mesh(vbQuad, DrawMode.Indexed);
        SetMesh(ref mesh);
        Tint = Color4.White;
        Pivot = new Vector2(0.5f, 0.5f);
        DisableCulling = true;
        RenderGroup = RenderGroup.UI;
    }

    /// <summary>
    /// The sprite dimensions. Default is the texture dimensions.
    /// Note: updating the size will also update the scale.
    /// </summary>
    public Vector2i Size
    {
        get => size;
        set
        {
            if ((Material?.Textures?.Length ?? 0) > 0)
            {
                var texture = Material!.Textures![0];
                SetScale(new Vector3((float)value.X / texture.Width, (float)value.Y / texture.Height, 1));
            }
            size = value;
        }
    }

    /// <summary>
    /// Gets or sets the frame inside the sprite texture that will be rendered, the default is the whole texture.
    /// </summary>
    public Rectangle SourceRectangle
    {
        get => sourceRectangle;
        set => sourceRectangle = value;
    }

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
        }
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
    /// Sets the sprites position.
    /// </summary>
    /// <param name="position"></param>
    public void SetPosition(in Vector2 position)
    {
        SetPosition(new Vector3(position.X, position.Y, 0));
    }

    /// <summary>
    /// Gets the sprites position.
    /// </summary>
    /// <param name="position"></param>
    public void GetPosition(out Vector2 position)
    {
        position = new Vector2(this.position.X, this.position.Y);
    }

    /// <summary>
    /// <inheritdoc/>
    /// Updating the scale will also update the sprite size.
    /// Note: sprites are 2D objects so the Z component is ignored.
    /// </summary>
    public override void SetScale(in Vector3 scale)
    {
        if ((Material?.Textures?.Length ?? 0) > 0)
        {
            var texture = Material!.Textures![0];
            size.X = (int)MathF.Round(scale.X * texture.Width);
            size.Y = (int)MathF.Round(scale.Y * texture.Height);
        }
        base.SetScale(scale);
    }

    /// <summary>
    /// Calculates the new projection matrix when the window is resized.
    /// </summary>
    /// <param name="scene"></param>
    /// <param name="e"></param>
    public override void OnResize(Scene scene, ResizeEventArgs e)
    {
        projection = Matrix4.CreateOrthographicOffCenter(0, e.Width, e.Height, 0, -1, 1);
        shader.SetMatrix4("projection", ref projection);
    }

    /// <summary>
    /// Calculates the world matrix (SROT).
    /// </summary>
    protected override void UpdateMatrix()
    {
        //  calculate scale, account for quad vertices in range [0,1] so we need to multiply with texture size
        //var spriteScale = new Vector3(scale.X * size.X, scale.Y * size.Y, 1);
        var spriteScale = new Vector3(size.X, size.Y, 1);
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
        if (previousDepthTestEnabled) GL.Disable(EnableCap.DepthTest);
        var texture = Material.Textures![0];
        shader.SetUniform4("sourceFrame",
            (float)sourceRectangle.X / texture.Width,
            1.0f - (float)(sourceRectangle.Y + sourceRectangle.Height) / texture.Height,
            (float)sourceRectangle.Width / texture.Width,
            (float)sourceRectangle.Height / texture.Height);
        shader.SetVector3("tint", ref tint);
        base.OnDraw(scene, elapsed);
        if (previousDepthTestEnabled) GL.Enable(EnableCap.DepthTest);
    }
}
