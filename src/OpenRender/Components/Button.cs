using OpenRender.Core;
using OpenRender.Core.Rendering.Text;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace OpenRender.Components;

/// <summary>
/// 2D sprite emulating a UI button. The buttons texture is treated as a 'nine slice'.
/// The UI interaction is very basic and just checks if the mouse is over the button disregarding any depth or overlays.
/// </summary>
public class Button : NineSlicePlane
{
    private bool isHovering;
    private bool isPressed;

    public static Button Create(string textureName, int ltrbSize, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(textureName);
        var (mesh, material) = CreateMeshAndMaterial(textureName, "Shaders/sprite.vert", "Shaders/nine-slice.frag");
        var btn = new Button(mesh, material, ltrbSize, width, height);
        return btn;
    }

    /// <inheritdoc/>
    public Button(Mesh mesh, Material material, int ltrbSize, int width, int height) : base(mesh, material, ltrbSize, ltrbSize, ltrbSize, ltrbSize, width, height) { }

    /// <inheritdoc/>
    public Button(Mesh mesh, Material material, string caption, int ltrbSize, int width, int height) : base(mesh, material, ltrbSize, ltrbSize, ltrbSize, ltrbSize, width, height)
    {
        Caption = caption;
    }

    public override void OnResize(Scene scene, ResizeEventArgs e)
    {
        if (Caption != null && TextRenderer != null)
        {
            TextRenderer.Projection = Matrix4.CreateOrthographicOffCenter(0, e.Width, e.Height, 0, -1, 1);
        }

        base.OnResize(scene, e);
    }

    public override void OnUpdate(Scene scene, double elapsed)
    {
        base.OnUpdate(scene, elapsed);
        HandleMouseState(scene.SceneManager.MouseState);
    }

    public override void OnDraw(double elapsed)
    {
        base.OnDraw(elapsed);
        if (Caption != null && TextRenderer != null)
        {
            var rect = TextRenderer.Measure(Caption);
            var x = (size.X - rect.Width) / 2;
            var y = (size.Y - rect.Height) / 2;
            TextRenderer.Render(Caption, transform.Position.X + x, transform.Position.Y + y, new(CaptionColor.R, CaptionColor.G, CaptionColor.B));
            Scene?.ResetMaterial();
        }
    }

    public string? Caption { get; set; }
    public Color4 CaptionColor { get; set; } = Color4.Black;

    public Action? OnClick { get; set; }

    public bool IsHovering => isHovering;

    public bool IsPressed => isPressed;

    public ITextRenderer? TextRenderer { get; set; }

    private void HandleMouseState(MouseState mouseState)
    {
        //  check if mouse is over the button
        isHovering = mouseState.X >= transform.Position.X && mouseState.X <= transform.Position.X + size.X &&
                     mouseState.Y >= transform.Position.Y && mouseState.Y <= transform.Position.Y + size.Y;

        if (!isHovering)
        {
            isPressed = false;
            return;
        }

        //  handle mouse clicks
        if (mouseState.IsButtonPressed(MouseButton.Left))
        {
            isPressed = true;
        }
        else if (isPressed)
        {
            //  check if mouse is released over the button
            if (!mouseState.IsButtonDown(MouseButton.Left))
            {
                isPressed = false;
                if (OnClick != null)
                    Scene?.AddAction(OnClick);
                else
                    Log.Warn("OnClick handler is null, did you forget to assign a click handler?");
            }
        }
    }
}
