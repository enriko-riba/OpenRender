using OpenRender.Components;
using OpenRender.Core;
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

        var ground = new Ground(100, 310, 170, 70, Color4.DarkGoldenrod);
        AddNode(ground);

        var animatedSprite = new AnimatedSprite("Resources/atlas.png");
        animatedSprite.SetPosition(new Vector2(770, 210));
        AddNode(animatedSprite);
        animatedSprite.AddAnimation("bomb", new Rectangle[] {
            new (64, 128, 64, 64),
            new (128, 128, 64, 64),
            new (192, 128, 64, 64),
            new (64, 192, 64, 64),
            new (128, 192, 64, 64),
            new (192, 192, 64, 64)
        });       
        animatedSprite.Play("bomb", 4);
        animatedSprite.Size = new Vector2i(50, 50);
    }

    public override void OnActivate()
    {
        SceneManager.Title = "Snake";
    }
}
