using OpenRender.Core.Rendering;
using OpenTK.Mathematics;

namespace SpyroGame.World;

public class Player
{
    private readonly ICamera camera;
    private Vector3 position;

    public Player(ICamera camera, Vector3 position)
    {
        this.camera = camera;
        Position = position;
        Direction = camera.Front;
    }

    public ICamera Camera => camera;

    public Vector3 Position {
        get => position;
        set { 
            position = value; 
            UpdateCamera();
        }
    }

    public Vector3 Direction { get; set; }

    public Vector3 ChunkLocalPosition { get; set; } = new Vector3(0, 0, 0);

    public Chunk? CurrentChunk { get; set; } = null;

    public BlockState? CurrentBlockBellow { get; set; } = null;

    /// <summary>
    /// If true, the player can walk through blocks and is not attached to the terrain.
    /// </summary>
    public bool IsGhostMode { get; set; } = false;

    public void AddRotation(float yawDegrees, float pitchDegrees, float rollDegrees)
    {
        camera.AddRotation(yawDegrees, pitchDegrees, rollDegrees);
        Direction = camera.Front;
    }

    internal void MoveForward(float movementPerSecond)
    {
        var dir = Vector3.Normalize(new Vector3(camera.Front.X, 0, camera.Front.Z));
        Position += dir * movementPerSecond;
        UpdateCamera();
    }

    internal void MoveLeft(float movementPerSecond)
    {
        Position -= Vector3.Cross(Direction, Vector3.UnitY) * movementPerSecond;
        UpdateCamera();
    }

    internal void MoveRight(float movementPerSecond)
    {
        Position += Vector3.Cross(Direction, Vector3.UnitY) * movementPerSecond;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        var p = Position;
        p.Y += 1.85f;   //  1.85 for eye height
        camera.Position = p;
    }
}
