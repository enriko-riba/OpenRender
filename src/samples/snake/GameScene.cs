using OpenRender.Components;
using OpenRender.Core.Rendering;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;
using snake.Logic;

namespace snake;

internal class GameScene : Scene
{
    public GameScene() : base() { }

    public override void Load()
    {
        base.Load();
        camera = new Camera2D(new Vector3(0, 0, 0), SceneManager.ClientSize.X, SceneManager.ClientSize.Y);

        var btn = new Button("Resources/btn.png", 16, 200, 120);
        btn.SetPosition(new Vector3(100, 100, 0));
        btn.Tint = Color4.BurlyWood;
        btn.OnClick = () => SceneManager.ActivateScene(nameof(MenuScene));
        AddNode(btn);

        var ground = new Ground(10, 10, 70, 70, Color4.DarkGoldenrod);
        AddNode(ground);
    }

    public override void OnActivate()
    {
        SceneManager.Title = "Snake";
    }
}
