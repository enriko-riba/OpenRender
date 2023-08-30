using OpenRender.SceneManagement;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace OpenRender.Components;

/// <summary>
/// 2D sprite emulating a UI button. The buttons texture is treated as a 'nine slice'.
/// The UI interaction is very basic and just checks if the mouse is over the button disregarding any depth or overlays.
/// </summary>
public class Button: NineSlicePlane
{
    private bool isHovering;
    private bool isPressed;
    
    /// <inheritdoc/>
    public Button(string textureName, int ltrbSize, int width, int height) : base(textureName, ltrbSize, ltrbSize, ltrbSize, ltrbSize, width, height) { }

    public override void OnUpdate(Scene scene, double elapsed)
    {
        base.OnUpdate(scene, elapsed);
        HandleMouseState(scene.SceneManager.MouseState);
    }

    public Action? OnClick { get; set; }    

    private void HandleMouseState(MouseState mouseState)
    {
        //  check if mouse is over the button
        var w = scale.X * size.Width;
        var h = scale.Y * size.Height;
        isHovering = mouseState.X >= position.X && mouseState.X <= position.X + w &&
                     mouseState.Y >= position.Y && mouseState.Y <= position.Y + h;

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
