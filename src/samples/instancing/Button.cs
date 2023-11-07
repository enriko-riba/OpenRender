using OpenRender.Core;
using OpenRender.Core.Textures;
using OpenTK.Mathematics;

namespace Samples.Snake;

/// <summary>
/// Simple wrapper around <see cref="OpenRender.Components.Button"/> that loads the buttons texture atlas consisting 
/// of three frames with normal, hover and pressed button states and applies the frame in <see cref="Button.Update"/>.
/// </summary>
internal class Button : OpenRender.Components.Button
{
    private const int BtnWidth = 160;
    private const int BtnHeight = 45;
    private const int BtnEdgeSize = 30;
    private static (Mesh mesh, Material material) mm;

    static Button()
    {
        mm = CreateMeshAndMaterial("Resources/btnAtlas.png");
    }
    
    public Button(string caption) : base(mm.mesh, mm.material, caption, BtnEdgeSize, BtnWidth, BtnHeight)
    {
        SourceRectangle = new Rectangle(0, 0, 200, 60);
        Update = (node, elapsed) =>
            {
                var btn = (node as Button)!;
                var rect = btn.SourceRectangle;
                rect.Y = btn.IsPressed ? 120 :
                            btn.IsHovering ? 60 : 0;
                btn.SourceRectangle = rect;
                btn.CaptionColor = btn.IsPressed ? Color4.YellowGreen : Color4.White;
            };
    }
}
