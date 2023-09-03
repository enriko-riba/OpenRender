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

    /// <inheritdoc/>
    public Button(string textureName, int ltrbSize, int width, int height) : base(textureName, ltrbSize, ltrbSize, ltrbSize, ltrbSize, width, height){}

    /// <inheritdoc/>
    public Button(string caption, string textureName, int ltrbSize, int width, int height) : this(textureName, ltrbSize, width, height)
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

    public override void OnDraw(Scene scene, double elapsed)
    {
        //  if the text renderer is used it binds its own texture without the scene rendering pipeline
        //  knowing about any material changes -  so we need to bind the texture manually here
        if (Caption != null && TextRenderer != null && (Material?.Textures?.Length??0) > 0)
        {
            var texture = Material!.Textures![0];
            var unit = scene.GetBatchedTextureUnit(texture);
            texture.Use(OpenTK.Graphics.OpenGL4.TextureUnit.Texture0 + unit);
            shader.SetInt(texture.UniformName, unit);
        }

        base.OnDraw(scene, elapsed);
        if (Caption != null && TextRenderer != null)
        {
            var rect = TextRenderer.Measure(Caption);
            var x = (size.X-rect.Width) / 2;
            var y = (size.Y-rect.Height) / 2;
            TextRenderer.Render(Caption, position.X + x, position.Y + y, new(CaptionColor.R, CaptionColor.G, CaptionColor.B));
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
        isHovering = mouseState.X >= position.X && mouseState.X <= position.X + size.X &&
                     mouseState.Y >= position.Y && mouseState.Y <= position.Y + size.Y;

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
                    OnClick.Invoke();
                else
                    Log.Warn("OnClick handler is null, did you forget to assign a click handler?");
            }
        }
    }
}
