using OpenTK.Windowing.GraphicsLibraryFramework;

namespace SpyroGame.Input;

public class KeyboardActionMapper
{
    private KeyboardAction[] actions = [];

    public void AddActions(KeyboardAction[] actions) => this.actions = [.. this.actions, .. actions];

    public void Update(KeyboardState keyboardState)
    {
        foreach (var action in actions)
        {
            //  if all keys are pressed, invoke the action
            if (action.Keys.All(keyboardState.IsKeyDown))
            {
                if (action.triggerOnChange && action.Keys.Any(keyboardState.WasKeyDown)) return;
                action.Action();
            }
        }
    }
}

public record KeyboardAction(string Name, Keys[] Keys, Action Action, bool triggerOnChange = true);