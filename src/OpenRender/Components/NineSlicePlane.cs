using OpenRender.Core;
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
    /// <param name="textureName"></param>
    /// <param name="leftWidth"></param>
    /// <param name="topHeight"></param>
    /// <param name="rightWidth"></param>
    /// <param name="bottomHeight"></param>
    /// <param name="width"></param>
    /// <param name="height"></param>
    /// <returns></returns>
    public static NineSlicePlane Create(string textureName, int leftWidth, int topHeight, int rightWidth, int bottomHeight, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(textureName);
        var (mesh, material) = CreateMeshAndMaterial(textureName, "Shaders/sprite.vert", "Shaders/nine-slice.frag");
        var nsp = new NineSlicePlane(mesh, material, leftWidth, topHeight, rightWidth, bottomHeight, width, height);
        return nsp;
    }

    /// <summary>
    /// Creates a new nine slice plane sprite.
    /// </summary>
    /// <param name="mesh"></param>
    /// <param name="material"></param>
    /// <param name="leftWidth"></param>
    /// <param name="topHeight"></param>
    /// <param name="rightWidth"></param>
    /// <param name="bottomHeight"></param>
    /// <param name="width"></param>
    /// <param name="height"></param>
    public NineSlicePlane(Mesh mesh, Material material, int leftWidth, int topHeight, int rightWidth, int bottomHeight, int width, int height) : base(mesh, material)
    {
        this.leftWidth = leftWidth;
        this.topHeight = topHeight;
        this.rightWidth = rightWidth;
        this.bottomHeight = bottomHeight;
        Tint = Color4.White;
        Size = new Vector2i(width, height);
        Material.Shader.SetMatrix4("projection", ref projection);
    }

    public override void OnDraw(double elapsed)
    {
        var shader = Material.Shader;
        shader.SetInt("leftWidth", leftWidth);
        shader.SetInt("topHeight", topHeight);
        shader.SetInt("rightWidth", rightWidth);
        shader.SetInt("bottomHeight", bottomHeight);
        shader.SetInt("width", size.X);
        shader.SetInt("height", size.Y);
        shader.SetInt("targetWidth", size.X);
        shader.SetInt("targetHeight", size.Y);
        base.OnDraw(elapsed);
    }
}
