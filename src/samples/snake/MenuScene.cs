using OpenRender.Components;
using OpenRender.Core;
using OpenRender.Core.Rendering;
using OpenRender.Core.Rendering.Text;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;

namespace Samples.Snake;

internal class MenuScene : Scene
{
    private readonly ITextRenderer textRenderer;

    public MenuScene(ITextRenderer textRenderer) : base()
    {
        this.textRenderer = textRenderer;
    }

    public override void Load()
    {
        base.Load();
        camera = new Camera2D(new Vector3(0, 0, 0), SceneManager.ClientSize.X, SceneManager.ClientSize.Y);

        var btn = new Button("Play")
        {
            TextRenderer = textRenderer,
            OnClick = () => SceneManager.ActivateScene(nameof(GameScene))
        };
        AddNode(btn);
        SceneManager.AddScene(new GameScene(textRenderer));
    }

    public override void OnActivate()
    {
        SceneManager.Title = "Snake - main menu";
    }
}
