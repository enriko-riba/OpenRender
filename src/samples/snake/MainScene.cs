using OpenRender;
using OpenRender.Components;
using OpenRender.Core;
using OpenRender.Core.Rendering;
using OpenRender.Core.Rendering.Text;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Samples.Snake.Logic;

using static Samples.Snake.Constants;

namespace Samples.Snake;

internal class MainScene : Scene
{
    private const int BtnWidth = 350;
    private const int BtnHeight = 75;

    private readonly ITextRenderer textRenderer;
    private readonly GameModel gameModel = new();
    private readonly Rectangle[] spriteFrames = new Rectangle[4];
    private readonly Dictionary<Vector2, Sprite> gridSprites = new();
    private readonly SnakeSprite snakeSprite;
    private Button btnResume = default!;
    private Button btnExit = default!;
    private Direction requestedDirection = Direction.None;

    public MainScene(ITextRenderer textRenderer) : base()
    {
        BackgroundColor = Color4.Purple;
        this.textRenderer = textRenderer;

        spriteFrames[(int)FrameType.Head] = new(128, 0, TileSourceSize, TileSourceSize);
        spriteFrames[(int)FrameType.Tail] = new(192, 0, TileSourceSize, TileSourceSize);
        spriteFrames[(int)FrameType.Body] = new(0, 0, TileSourceSize, TileSourceSize);
        spriteFrames[(int)FrameType.BodyCorner] = new(64, 0, TileSourceSize, TileSourceSize);
        snakeSprite = new(gameModel.SnakeTiles);
    }

    public override void Load()
    {
        SceneManager.CursorState = OpenTK.Windowing.Common.CursorState.Hidden;

        base.Load();
        camera = new Camera2D(new Vector3(0, 0, 0), Width, Height);
        AddNode(new Ground(0, Margin, Width, Height, Color4.DarkOliveGreen));
        AddNode(snakeSprite);
        gameModel.NextLevel();
        CreateObjects();

        var btnX = (Width - BtnWidth) / 2;
        var btnY = Height / 2;
        btnResume = new BigButton("Start - or press space")
        {          
            TextRenderer = textRenderer,
            OnClick = HandleStartClick,
            Tint = new Color4(0.75f, 0.5f, 0.75f, 1),
        };
        btnResume.SetPosition(new(btnX, btnY));
        AddNode(btnResume);

        btnExit = new BigButton("Exit")
        {
            TextRenderer = textRenderer,
            OnClick = SceneManager.Close,
            Tint = new Color4(1, 0.4f, 0.4f, 1),
        };
        btnExit.SetPosition(new(btnX, btnY + BtnHeight * 1.25f));
        AddNode(btnExit);        
    }

    public override void RenderFrame(double elapsedSeconds)
    {
        base.RenderFrame(elapsedSeconds);
        SceneManager.CursorState = gameModel.State == GameState.Started ?
            OpenTK.Windowing.Common.CursorState.Hidden :
            OpenTK.Windowing.Common.CursorState.Normal;
        btnResume.IsVisible = gameModel.State != GameState.Started;
        btnExit.IsVisible = btnResume.IsVisible;    
        switch (gameModel.State)
        {
            case GameState.Died:
                DrawMenuTextCentered(0, -50, "Congratz, you just killed your snake!", 20, Color4.CornflowerBlue);
                break;
            case GameState.LevelCompleted:
                DrawMenuTextCentered(0, -50, $"LEVEL {gameModel.Level} COMPLETED!", 20, Color4.Gold);
                break;
        }
        DrawText(5, 4, $"fps: {SceneManager.Fps:N0}", 20, Color4.Lime);
        DrawText(140, 4, $"{gameModel.Velocity:N2} T/Sec", 20, Color4.Lime);
        DrawText(300, 4, $"{gameModel.MoveDurationSeconds:N2} Sec/T", 20, Color4.Lime);
        DrawMenuText(5, 25, $"Level: {gameModel.Level}  Food: {gameModel.FoodEaten}/{gameModel.FoodCount}", 20, Color4.Lime);
    }

    public override void UpdateFrame(double elapsedSeconds)
    {
        base.UpdateFrame(elapsedSeconds);
        if (gameModel.State == GameState.Started)
        {
            HandleStartedInput();
            RemoveDeadSprites();
        }
        else
        {
            HandleNotStartedInput();
        }
    }

    public override void OnActivate()
    {
        SceneManager.Title = "Snake - game";
        base.OnActivate();
    }

    private void RemoveDeadSprites()
    {
        for (var j = 0; j < GameModel.GridTilesY; j++)
        {
            for (var i = 0; i < GameModel.GridTilesX; i++)
            {
                if (gameModel.Grid[i, j] == TileType.Empty && gridSprites.ContainsKey(new(i, j)))
                {
                    var child = gridSprites[new(i, j)];
                    RemoveNode(child);
                }
            }
        }
    }

    private void CreateObjects()
    {
        foreach (var kvp in gridSprites)
        {
            RemoveNode(kvp.Value);
        }
        gridSprites.Clear();

        for (var j = 0; j < GameModel.GridTilesY; j++)
        {
            for (var i = 0; i < GameModel.GridTilesX; i++)
            {
                var x = i * TileSize;
                var y = j * TileSize + Margin;

                switch (gameModel.Grid[i, j])
                {
                    case TileType.Block:
                        var sprBlock = new Sprite("Resources/atlas.png")
                        {
                            SourceRectangle = new Rectangle(0, 64, TileSourceSize, TileSourceSize),
                            Size = new(TileSize, TileSize),
                        };
                        sprBlock.SetPosition(new(x, y));
                        AddNode(sprBlock);
                        gridSprites[new(i, j)] = sprBlock;
                        break;

                    case TileType.Bomb:
                        var animatedSprite = new AnimatedSprite("Resources/atlas.png")
                        {
                            Size = new Vector2i(TileSize, TileSize),
                        };
                        animatedSprite.SetPosition(new(x, y));
                        animatedSprite.AddAnimation("bomb", new Rectangle[] {
                            new (64, 128, TileSourceSize, TileSourceSize),
                            new (128, 128, TileSourceSize, TileSourceSize),
                            new (192, 128, TileSourceSize, TileSourceSize),
                            new (64, 192, TileSourceSize, TileSourceSize),
                            new (128, 192, TileSourceSize, TileSourceSize),
                            new (192, 192, TileSourceSize, TileSourceSize)
                        });
                        animatedSprite.Play("bomb", 4);
                        AddNode(animatedSprite);
                        gridSprites[new(i, j)] = animatedSprite;
                        break;

                    case TileType.FoodFrog:
                        var sprFrog = new AnimatedSprite("Resources/atlas.png")
                        {
                            Size = new Vector2i(TileSize, TileSize),
                        };
                        sprFrog.SetPosition(new(x, y));
                        sprFrog.AddAnimation("idle", new Rectangle[] {
                            new (192, 64, TileSourceSize, TileSourceSize),
                            new (0, 128, TileSourceSize, TileSourceSize),
                        });
                        sprFrog.Play("idle", 1);
                        AddNode(sprFrog);
                        gridSprites[new(i, j)] = sprFrog;
                        break;

                    case TileType.FoodApple:
                        var sprApple = new Sprite("Resources/atlas.png")
                        {
                            SourceRectangle = new Rectangle(0, 192, TileSourceSize, TileSourceSize),
                            Size = new(TileSize, TileSize),
                        };
                        sprApple.SetPosition(new(x, y));
                        AddNode(sprApple);
                        gridSprites[new(i, j)] = sprApple;
                        break;
                };
            }
        }

    }

    private void HandleSnakeDestroyed()
    {
        requestedDirection = Direction.None;
    }

    private DateTime nextMove = DateTime.Now;
    private void HandleStartedInput()
    {
        var direction = ReadGameInput();
        if (direction != Direction.None) requestedDirection = direction;

        if (nextMove <= DateTime.Now)
        {
            nextMove = DateTime.Now.AddSeconds(gameModel.MoveDurationSeconds);
            if (!gameModel.MoveSnake(requestedDirection == Direction.None ? gameModel.SnakeDirection : requestedDirection))
            {
                HandleSnakeDestroyed();
            }
        }
    }

    private void HandleNotStartedInput()
    {
        var kbd = SceneManager.KeyboardState;
        if (kbd.IsKeyDown(Keys.Space))
        {
            HandleStartClick();
        }
    }

    private void HandleStartClick()
    {
        if (gameModel.State == GameState.Died)
        {
            requestedDirection = Direction.None;
            gameModel.RestartLevel();
        }
        else if (gameModel.State == GameState.LevelCompleted)
        {
            requestedDirection = Direction.None;
            gameModel.NextLevel();
        }
        gameModel.TogglePause();
        CreateObjects();
    }

    private Direction ReadGameInput()
    {
        var kbd = SceneManager.KeyboardState;
        var direction = kbd.IsKeyDown(Keys.Right) ? Direction.East :
                              kbd.IsKeyDown(Keys.Left) ? Direction.West :
                              kbd.IsKeyDown(Keys.Up) ? Direction.North :
                              kbd.IsKeyDown(Keys.Down) ? Direction.South :
                              Direction.None;

        //  prevent 180 degree turns
        if (direction != Direction.None)
        {
            direction = (direction == Direction.North && gameModel.SnakeDirection == Direction.South) ? Direction.None :
                        (direction == Direction.South && gameModel.SnakeDirection == Direction.North) ? Direction.None :
                        (direction == Direction.West && gameModel.SnakeDirection == Direction.East) ? Direction.None :
                        (direction == Direction.East && gameModel.SnakeDirection == Direction.West) ? Direction.None : direction;
        }

        return direction;
    }

    private void DrawMenuTextCentered(int xOffset, int yOffset, string text, int fontSize, Color4 color)
    {        
        var size = textRenderer.Measure(text, fontSize);
        Log.Debug($"DrawMenuTextCentered: size: {size.Width}, w:{Width}, h:{Height}, x:{xOffset + (Width - size.Width) / 2}");
        textRenderer.Render(text, fontSize, xOffset + (Width - size.Width) / 2, yOffset + (Height - size.Height) / 2, color.ToVector3());
    }

    private void DrawMenuText(int xOffset, int yOffset, string text, int fontSize, Color4 color)
    {
        var size = textRenderer.Measure(text);
        textRenderer.Render(text, fontSize, xOffset, yOffset + (TileSize - size.Height) / 2, color.ToVector3());
    }

    private void DrawText(int xOffset, int yOffset, string text, int fontSize, Color4 color)
    {
        textRenderer.Render(text, fontSize, xOffset, yOffset, color.ToVector3());
    }

    private class BigButton : Button
    {
        public BigButton(string caption): base(caption, "Resources/btnAtlas.png", 30, BtnWidth, BtnHeight)
        {
            SourceRectangle = new Rectangle(0, 0, 200, 60);
            Update = (node, elapsed) =>
            {
                var btn = (node as Button)!;
                var rect = btn.SourceRectangle;
                rect.Y = btn.IsPressed ? 120 :
                            btn.IsHovering ? 60 : 0;
                btn.SourceRectangle = rect;
                btn.CaptionColor = btn.IsPressed ? Color4.YellowGreen : Color4.White;
            };            
        }
    }
}
