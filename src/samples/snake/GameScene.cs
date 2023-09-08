﻿using OpenRender;
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

internal class GameScene : Scene
{

    private readonly ITextRenderer textRenderer;
    private readonly GameModel gameModel = new();
    private readonly Rectangle[] spriteFrames = new Rectangle[4];
    private readonly Dictionary<Vector2, Sprite> gridSprites = new();
    private readonly SnakeSprite snakeSprite;
    private Direction requestedDirection = Direction.None;

    public GameScene(ITextRenderer textRenderer) : base()
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
        camera = new Camera2D(new Vector3(0, 0, 0), SceneManager.ClientSize.X, SceneManager.ClientSize.Y);
        AddNode(new Ground(0, Margin, SceneManager.ClientSize.X, SceneManager.ClientSize.Y, Color4.DarkGreen));
        AddNode(snakeSprite);
        gameModel.NextLevel();
        CreateObjects();
    }

    public override void RenderFrame(double elapsedSeconds)
    {
        base.RenderFrame(elapsedSeconds);
        switch (gameModel.State)
        {
            case GameState.Paused:
                DrawMenuTextCentered(0, 20, "press SPACE to start.", 30, Color4.Gold);
                break;
            case GameState.Died:
                DrawMenuTextCentered(0, 20, "Congratz, you just killed your snake! Press SPACE to restart.", 30, Color4.Gold);
                break;
            case GameState.LevelCompleted:
                DrawMenuTextCentered(0, SceneManager.ClientSize.X / 2 - Margin, $"LEVEL {gameModel.Level} COMPLETED!\nPress SPACE to continue.", 50, Color4.Gold);
                break;
        }
        DrawText(5, 4, $"fps: {SceneManager.Fps:N0}", 20, Color4.Lime);
        DrawText(140, 4, $"{gameModel.Velocity:N2} T/Sec", 20, Color4.Lime);
        DrawText(300, 4, $"{gameModel.MoveDurationSeconds:N2} Sec/T", 20, Color4.Lime);
        DrawMenuText(5, 25, $"Level: {gameModel.Level}  Food: {gameModel.FoodEaten}/{gameModel.FoodCount}", 30, Color4.Lime);
    }

    public override void UpdateFrame(double elapsedSeconds)
    {
        base.UpdateFrame(elapsedSeconds);
        if (gameModel.State == GameState.Started)
        {
            HandleStartedInput();
        }
        else
        {
            HandleNotStartedInput();
        }
        RemoveDeadSprites();
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
        //RemoveAllNodes();
        foreach(var kvp in gridSprites)
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
                        var sprFrog = new Sprite("Resources/atlas.png")
                        {
                            SourceRectangle = new Rectangle(0, 128, TileSourceSize, TileSourceSize),
                            Size = new(TileSize, TileSize),
                        };
                        sprFrog.SetPosition(new(x, y));
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
        var w = SceneManager.ClientSize.X;
        var size = textRenderer.Measure(text);
        textRenderer.Render(text, xOffset + (w - size.Width) / 2, yOffset + (TileSize - size.Height) / 2, color.ToVector3());
    }

    private void DrawMenuText(int xOffset, int yOffset, string text, int fontSize, Color4 color)
    {
        var size = textRenderer.Measure(text);
        textRenderer.Render(text, xOffset, yOffset + (TileSize - size.Height) / 2, color.ToVector3());
    }

    private void DrawText(int xOffset, int yOffset, string text, int fontSize, Color4 color)
    {
        textRenderer.Render(text, xOffset, yOffset, color.ToVector3());
    }
}
