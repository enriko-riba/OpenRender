using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SpyroGame.Shared.Input;

namespace SpyroGame.Components;

public static class PlayerInputMapper
{
    public static PlayerInputState BuildState(KeyboardState keyboard, bool isGhostMode)
    {
        var moveAxes = Vector2.Zero;
        if (keyboard.IsKeyDown(Keys.W)) moveAxes.Y += 1;
        if (keyboard.IsKeyDown(Keys.S)) moveAxes.Y -= 1;
        if (keyboard.IsKeyDown(Keys.D)) moveAxes.X += 1;
        if (keyboard.IsKeyDown(Keys.A)) moveAxes.X -= 1;

        var sprintHeld = false;
        var crouchHeld = false;
        var verticalAxis = 0.0f;

        var shiftHeld = keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift);
        var ctrlHeld = keyboard.IsKeyDown(Keys.LeftControl) || keyboard.IsKeyDown(Keys.RightControl);
        var spaceHeld = keyboard.IsKeyDown(Keys.Space);

        if (isGhostMode)
        {
            // Ghost mode: classic fly controls.
            // Space = up; Shift/Ctrl = down.
            if (spaceHeld) verticalAxis += 1.0f;
            if (shiftHeld) verticalAxis -= 1.0f;
            if (ctrlHeld) verticalAxis -= 1.0f;
        }
        else
        {
            sprintHeld = shiftHeld;
            crouchHeld = ctrlHeld;
        }

        return new PlayerInputState(
            MoveAxes: moveAxes,
            VerticalAxis: verticalAxis,
            SprintHeld: sprintHeld,
            CrouchHeld: crouchHeld);
    }

    public static PlayerInputCommand Build(KeyboardState keyboard, MouseState mouse, bool isGhostMode, Vector2 lookDelta, bool? setGhostMode)
    {
        var jumpPressed = !isGhostMode && keyboard.IsKeyPressed(Keys.Space);

        var state = BuildState(keyboard, isGhostMode);

        int? selectSlot = null;
        if (keyboard.IsKeyPressed(Keys.D1)) selectSlot = 0;
        if (keyboard.IsKeyPressed(Keys.D2)) selectSlot = 1;
        if (keyboard.IsKeyPressed(Keys.D3)) selectSlot = 2;
        if (keyboard.IsKeyPressed(Keys.D4)) selectSlot = 3;
        if (keyboard.IsKeyPressed(Keys.D5)) selectSlot = 4;
        if (keyboard.IsKeyPressed(Keys.D6)) selectSlot = 5;
        if (keyboard.IsKeyPressed(Keys.D7)) selectSlot = 6;
        if (keyboard.IsKeyPressed(Keys.D8)) selectSlot = 7;
        if (keyboard.IsKeyPressed(Keys.D9)) selectSlot = 8;

        var scrollDelta = 0;
        if (mouse.ScrollDelta.Y != 0)
        {
            scrollDelta = -Math.Sign(mouse.ScrollDelta.Y);
        }

        return new PlayerInputCommand(
            MoveAxes: state.MoveAxes,
            VerticalAxis: state.VerticalAxis,
            LookDelta: lookDelta,
            JumpPressed: jumpPressed,
            SprintHeld: state.SprintHeld,
            CrouchHeld: state.CrouchHeld,
            SetGhostMode: setGhostMode,
            SelectHotbarSlot: selectSlot,
            HotbarScrollDelta: scrollDelta);
    }
}
