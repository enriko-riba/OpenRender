using OpenRender.Components;
using OpenRender.Core.Rendering;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;

namespace snake;

internal class MenuScene : Scene
{
    public MenuScene() : base() { }

    public override void Load()
    {
        base.Load();
        camera = new Camera2D(new Vector3(0, 0, 0), SceneManager.ClientSize.X, SceneManager.ClientSize.Y);

        var btn = new Button("Resources/btn.png", 16, 200, 80);
        btn.SetPosition(new Vector3(10, 10, 0));
        btn.OnClick = () => SceneManager.ActivateScene(nameof(GameScene));
        AddNode(btn);
        SceneManager.AddScene(new GameScene());
    }

    public override void OnActivate()
    {
        SceneManager.Title = "Snake - main menu";
    }
}
