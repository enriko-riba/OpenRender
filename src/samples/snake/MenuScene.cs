using OpenRender.Components;
using OpenRender.Core;
using OpenRender.Core.Rendering;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;

namespace Samples.Snake;

internal class MenuScene : Scene
{
    public MenuScene() : base() { }

    public override void Load()
    {
        base.Load();
        camera = new Camera2D(new Vector3(0, 0, 0), SceneManager.ClientSize.X, SceneManager.ClientSize.Y);

        //var btn = new Button("Resources/btn.png", 16, 200, 80);
        var btn = new Button("Resources/btnAtlas.png", 60, 200, 80)
        {
            SourceRectangle = new Rectangle(0, 0, 400, 120),
            Update = (node, elapsed) =>
            {
                var btn = (node as Button)!;
                var rect = btn.SourceRectangle;
                rect.Y = btn.IsPressed ? 240 :
                         btn.IsHovering ? 120 : 0;
                btn.SourceRectangle = rect;
            }
        };
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
