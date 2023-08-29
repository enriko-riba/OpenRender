using OpenRender.Components;
using OpenRender.Core.Rendering;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;

namespace snake;

internal class MenuScene : Scene
{
    public MenuScene() : base(nameof(MenuScene)) { }

    public override void Load()
    {
        base.Load();
        SceneManager.Title = "Snake - main menu";
        camera = new Camera2D(new Vector3(0, 0, 0), SceneManager.ClientSize.X, SceneManager.ClientSize.Y);

        var nsp = new NineSlicePlane("Resources/btn.png", 16, 180, 60);
        nsp.SetPosition(new Vector3(10, 10, 0));
        AddNode(nsp);
    }
}
