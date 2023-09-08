using System.Diagnostics.CodeAnalysis;

namespace Samples.Snake.Logic;

public class GameModel
{
    public const int GridTilesX = 60;
    public const int GridTilesY = 32;

    private const float BaseVelocityTilesPerSecond = 4f;
    private const float VelocityLevelIncreaseSecond = 0.3f;

    private readonly LinkedList<SnakeTile> snakeTiles = new();
    
    public GameModel()
    {
        InitGrid();
        InitSnake();
    }

    public GameState State { get; private set; } = GameState.Paused;

    public int Level { get; private set; }

    public TileType[,] Grid { get; private set; }

    public void TogglePause()
    {
        State = State == GameState.Started ? GameState.Paused : GameState.Started;
    }

    public void NextLevel()
    {
        State = GameState.Paused;
        Level++;
        RestartLevel();
    }

    public void RestartLevel()
    {
        InitGrid();
        InitSnake();
        InitObjects();
        FoodEaten = 0;
        SnakeDirection = Direction.West;
    }

    public IEnumerable<SnakeTile> SnakeTiles => snakeTiles;

    public float Velocity => BaseVelocityTilesPerSecond + (Level * VelocityLevelIncreaseSecond) + (FoodEaten * VelocityLevelIncreaseSecond / 2f);
    
    public float MoveDurationSeconds => 1f / Velocity;

    public int FoodCount => 8 + Level;

    public int FoodEaten { get; private set; }

    #region Initialization
    [MemberNotNull(nameof(Grid))]
    private void InitGrid()
    {
        Grid = new TileType[GridTilesX, GridTilesY];
        for (var j = 0; j < GridTilesY; j++)
        {
            for (var i = 0; i < GridTilesX; i++)
            {
                Grid[i, j] = (i, j) switch
                {
                    { i: <= 0 } => TileType.Block,
                    { i: >= (GridTilesX - 1) } => TileType.Block,
                    { j: <= 0 } => TileType.Block,
                    { j: >= (GridTilesY - 1) } => TileType.Block,
                    _ => TileType.Empty
                };
            }
        }
    }

    private void InitSnake()
    {
        snakeTiles.Clear();
        var x = GridTilesX / 2;
        var y = GridTilesY / 2;
        var bodyTiles = Level + 1;
        x -= bodyTiles / 2;
        snakeTiles.AddLast(new SnakeTile(x - 1, y, Direction.West, FrameType.Head));
        for (var i = 0; i < bodyTiles; i++)
        {
            snakeTiles.AddLast(new SnakeTile(x + i, y, Direction.West, FrameType.Body));

        }
        snakeTiles.AddLast(new SnakeTile(x + bodyTiles, y, Direction.West, FrameType.Tail));        
    }

    private void InitObjects()
    {
        int x;
        int y;
        for (var i = 0; i < FoodCount; i++)
        {
            do
            {
                x = Random.Shared.Next(1, GridTilesX - 1);
                y = Random.Shared.Next(1, GridTilesY - 1);
            } while (Grid[x, y] != TileType.Empty);
            Grid[x, y] = (TileType)Random.Shared.Next((int)TileType.FoodFrog, 1 + (int)TileType.FoodApple);
        }

        var bombCount = 1 + (Level / 3);
        for (var i = 0; i < bombCount; i++)
        {
            do
            {
                x = Random.Shared.Next(2, GridTilesX - 2);
                y = Random.Shared.Next(2, GridTilesY - 2);
            } while (Grid[x, y] != TileType.Empty);
            Grid[x, y] = TileType.Bomb;
        }
    }
    #endregion

    #region Snake movement
    public Direction SnakeDirection { get; private set; } = Direction.West;

    /// <summary>
    /// Moves the snake one tile in the given direction.
    /// </summary>
    /// <param name="direction"></param>
    /// <returns>true if the snake has moved, false if it was blocked</returns>
    public bool MoveSnake(Direction direction)
    {
        var head = snakeTiles.First!;
        SnakeDirection = direction;

        //  create the head node on new position and check for food collision
        var currentSnakeTile = CreateHeadTile(head.Value, direction);
        
        //  check for collisions       
        var isCollided = HasHeadCollided(currentSnakeTile);
        if (isCollided)
        {
            State = GameState.Died;
            return false;
        }

        var shouldGrow = HandleFoodCollision(currentSnakeTile);

        //  movement algorithm:
        //  1. save current nodes position & direction into 'currentSnakeTile'
        //  2. swap current nodes position & direction with previous values
        //  3. update frame type if direction has changed
        //  in addition handle inserting new tile on growth and exit on next iteration
        //  repeat steps for all nodes
        var node = head;
        do
        {
            SwapTileValues(node.Value, currentSnakeTile);

            var tile = node.Value;
            tile.FrameType = node.Previous is null ? FrameType.Head :
                             node.Next is null ? FrameType.Tail :
                             tile.Direction == node.Previous.Value.Direction ? FrameType.Body : FrameType.BodyCorner;

            if (tile.FrameType == FrameType.BodyCorner)
            {
                tile.CornerDirection = CalcCornerDirection(node.Previous!.Value.Direction, tile.Direction);
            }

            //  for snake growth just insert a new node after head, the correct 
            //  frame type and corner direction will be set in next loop iteration
            if (shouldGrow)
            {
                //  stop processing further nodes since the newly inserted node (after head) is processed
                if (node.Previous == head) break;

                SnakeTile newTile = new(currentSnakeTile.X, currentSnakeTile.Y, currentSnakeTile.Direction, FrameType.Body);
                snakeTiles.AddAfter(node, newTile);
            }

            node = node.Next;
        }
        while (node != null);

        //  make sure tail has the direction of previous tile
        snakeTiles.Last!.Value.Direction = snakeTiles.Last.Previous!.Value.Direction;

        return true;
    }

    /// <summary>
    /// Checks if the head has collided with a blocker tile or the snakes body.
    /// </summary>
    /// <param name="headTile"></param>
    /// <returns>true if snake has collided</returns>
    private bool HasHeadCollided(SnakeTile headTile)
    {
        var isCollision = Grid[headTile.X, headTile.Y] is TileType.Block or TileType.Bomb;
        if (!isCollision)
        {
            foreach (var tile in snakeTiles.Skip(1))
            {
                if (headTile.X == tile.X && headTile.Y == tile.Y)
                {
                    isCollision = true;
                    break;
                }
            }
        }
        return isCollision;
    }

    /// <summary>
    /// Checks if the snake has collided with a food tile, clears the tile and updates state.
    /// </summary>
    /// <param name="head"></param>
    /// <returns>true if the snake has collided with a food tile</returns>
    private bool HandleFoodCollision(SnakeTile head)
    {
        if (Grid[head.X, head.Y] >= TileType.FoodFrog)
        {
            FoodEaten++;
            Grid[head.X, head.Y] = TileType.Empty;

            if (FoodEaten == FoodCount)
            {
                State = GameState.LevelCompleted;
            }
            return true;
        }
        return false;
    }

    private static void SwapTileValues(SnakeTile a, SnakeTile b)
    {
        var x = a.X;
        var y = a.Y;
        var d = a.Direction;

        a.X = b.X;
        a.Y = b.Y;
        a.Direction = b.Direction;

        b.X = x;
        b.Y = y;
        b.Direction = d;

        //(a.X, b.X) = (b.X, a.X);
        //(a.Y, b.Y) = (b.Y, a.Y);
        //(a.Direction, b.Direction) = (b.Direction, a.Direction);
    }

    private static Direction CalcCornerDirection(Direction prevDir, Direction bodyDir)
    {
        if (prevDir == Direction.South && bodyDir == Direction.East) return Direction.East;
        if (prevDir == Direction.South && bodyDir == Direction.West) return Direction.North;
        if (prevDir == Direction.North && bodyDir == Direction.East) return Direction.South;
        if (prevDir == Direction.North && bodyDir == Direction.West) return Direction.West;

        if (prevDir == Direction.East && bodyDir == Direction.South) return Direction.West;
        if (prevDir == Direction.East && bodyDir == Direction.North) return Direction.North;
        if (prevDir == Direction.West && bodyDir == Direction.South) return Direction.South;
        if (prevDir == Direction.West && bodyDir == Direction.North) return Direction.East;

        throw new Exception("invalid direction combination!");
    }

    private static SnakeTile CreateHeadTile(SnakeTile currentTile, Direction headDirection)
    {
        var dx = headDirection switch
        {
            Direction.East => 1,
            Direction.West => -1,
            _ => 0
        };
        var dy = headDirection switch
        {
            Direction.South => 1,
            Direction.North => -1,
            _ => 0
        };
        var x = currentTile.X + dx;
        var y = currentTile.Y + dy;
        var type = FrameType.Head;
        return new(x, y, headDirection, type);
    }
    #endregion
}
