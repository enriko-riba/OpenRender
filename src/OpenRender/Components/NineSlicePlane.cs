using OpenRender.Core.Rendering;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;

namespace OpenRender.Components;

/// <summary>
/// 2D sprite where the texture is treated as a 'nine slice'.
/// The nine slice frames are defined by the left width, top height, right width and bottom height size.
/// Texture corners are not stretched, the rest of the texture is stretched to fit the target width and height.
/// |---|-----------------|---|-------------
/// |lw | stretched width | rw| top height
/// |---|-----------------|---|-------------
/// |   |                 |   | 
/// |str| stretched width |str|
/// | h | and height      | h |
/// |   |                 |   | 
/// |---|-----------------|---|--------------
/// |lw | stretched width | rw| bottom height
/// |---|-----------------|---|--------------
/// </summary>
public class NineSlicePlane : Sprite
{
    private readonly int leftWidth;
    private readonly int topHeight;
    private readonly int rightWidth;
    private readonly int bottomHeight;

    /// <summary>
    /// Creates a new nine slice plane sprite.
    /// </summary>
    /// <param name="textureName">the texture resource</param>
    /// <param name="ltrbSize">uniform size applied to all texture edges (left, top, right, bottom)</param>
    /// <param name="width"></param>
    /// <param name="height"></param>
    public NineSlicePlane(string textureName, int ltrbSize, int width, int height) : this(textureName, ltrbSize, ltrbSize, ltrbSize, ltrbSize, width, height) { }

    public NineSlicePlane(string textureName, int leftWidth, int topHeight, int rightWidth, int bottomHeight, int width, int height) : base(textureName)
    {
        this.leftWidth = leftWidth;
        this.topHeight = topHeight;
        this.rightWidth = rightWidth;
        this.bottomHeight = bottomHeight;
        shader = new Shader("Shaders/sprite.vert", "Shaders/nine-slice.frag");
        shader.SetMatrix4("projection", ref projection);
        Material.Shader = shader;
        Tint = Color4.White;    //  need to re apply the tint in order to setup the shaders uniform
        Size = new Vector2i(width, height);
    }

    public override void OnDraw(Scene scene, double elapsed)
    {
        shader.SetInt("leftWidth", leftWidth);
        shader.SetInt("topHeight", topHeight);
        shader.SetInt("rightWidth", rightWidth);
        shader.SetInt("bottomHeight", bottomHeight);
        shader.SetInt("width", size.X);
        shader.SetInt("height", size.Y);
        shader.SetInt("targetWidth", size.X);
        shader.SetInt("targetHeight", size.Y);
        base.OnDraw(scene, elapsed);
    }
}
