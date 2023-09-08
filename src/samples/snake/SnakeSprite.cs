using OpenRender.Components;
using OpenRender.Core;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;
using Samples.Snake.Logic;
using static Samples.Snake.Constants;

namespace Samples.Snake;
internal class SnakeSprite : Sprite
{
    private readonly IEnumerable<SnakeTile> snakeTiles;
    private readonly Rectangle[] spriteFrames = new Rectangle[4];

    public SnakeSprite(IEnumerable<SnakeTile> snakeTiles) : base("Resources/atlas.png")
    {
        this.snakeTiles = snakeTiles;
        spriteFrames[(int)FrameType.Head] = new(128, 0, TileSourceSize, TileSourceSize);
        spriteFrames[(int)FrameType.Tail] = new(192, 0, TileSourceSize, TileSourceSize);
        spriteFrames[(int)FrameType.Body] = new(0, 0, TileSourceSize, TileSourceSize);
        spriteFrames[(int)FrameType.BodyCorner] = new(64, 0, TileSourceSize, TileSourceSize);
        Size = new(TileSize, TileSize);
    }

    public override void OnDraw(Scene scene, double elapsed)
    {
        foreach (var tile in snakeTiles)
        {
            Vector2 position = new(tile.X * TileSize, tile.Y * TileSize + Margin);
            var src = spriteFrames[(int)tile.FrameType];
            var rotation = tile.FrameType == FrameType.BodyCorner
                ? tile.CornerDirection switch
                {
                    Direction.South => 90,
                    Direction.West => 180,
                    Direction.North => 270,
                    _ => 0
                }
                : tile.Direction switch
                {
                    Direction.South => 90,
                    Direction.West => 180,
                    Direction.North => 270,
                    _ => 0
                };
            SetPosition(position);
            SourceRectangle = src;
            AngleRotation = rotation;
            shader.SetMatrix4("model", ref worldMatrix);
            base.OnDraw(scene, elapsed);
        }
        base.OnDraw(scene, elapsed);
    }
}
