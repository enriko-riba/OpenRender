using OpenRender;
using OpenRender.Core.Rendering;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SpyroGame.Input;

namespace SpyroGame.World;

public class Player
{
    private const float HalfWidth = 0.4f;
    private const float Height = 1.85f;
    private const float EyeHeight = 1.75f;
    private const float Gravity = -9.8f;

    private const float MovementSpeed = 2;
    private const float RotationSpeed = 10;

    private static readonly Vector3[] bottomCornerOffsets = [
            new Vector3(-HalfWidth, 0, -HalfWidth), // northwest
            new Vector3(-HalfWidth, 0, +HalfWidth), // southwest
            new Vector3(+HalfWidth, 0, -HalfWidth), // northeast
            new Vector3(+HalfWidth, 0, +HalfWidth), // southeast
        ];

    private readonly ICamera camera;
    private readonly VoxelWorld world;
    private Vector3 position;

    private bool isGrounded;
    private bool isJumping;
    private float velocityY;
    private Vector3 requestedMovement;
    private Vector3 requestedRotation;
    private BlockState? pickedBlock = null;
    private readonly KeyboardActionMapper kbdActions = new();

    public Player(ICamera camera, Vector3 position, VoxelWorld world)
    {
        this.camera = camera;
        this.world = world;
        Position = position;
        Direction = camera.Front;

        if (world.GetChunkByGlobalPosition(Position, out var chunk))
        {
            CurrentChunk = chunk;
            ChunkLocalPosition = (Vector3i)Position - chunk!.Position;
            var height = chunk!.GetTerrainHeightAt((int)ChunkLocalPosition.X, (int)ChunkLocalPosition.Z);
            Position = new(Position.X, height, Position.Z);
            isGrounded = true;
        }
        else
        {
            isGrounded = false;
        }

        kbdActions.AddActions([
            new KeyboardAction("fly mode", [Keys.F], () => IsGhostMode = !IsGhostMode),
            new KeyboardAction("forward", [Keys.W], MoveForward, false),
            new KeyboardAction("left", [Keys.A], MoveLeft, false),
            new KeyboardAction("right", [Keys.D], MoveRight, false),
            new KeyboardAction("back", [Keys.S], MoveBack, false),
            new KeyboardAction("jump", [Keys.Space], Jump),
            new KeyboardAction("rot CCW", [Keys.Q], () => AddRotation(0, 0, -1), false),
            new KeyboardAction("rot CW", [Keys.E], () => AddRotation(0, 0, 1), false),
        ]);
    }

    internal bool IsJumping => isJumping;
    internal bool IsGrounded => isGrounded;
    internal float VelocityY => velocityY;
    internal Vector3 RequestedMovement => requestedMovement;
    internal BlockState? PickedBlock => pickedBlock;

    public void Update(double elapsedSeconds, KeyboardState keyboardState)
    {
        elapsedSeconds = MathF.Min((float)elapsedSeconds, 0.2f);
        kbdActions.Update(keyboardState);

        var pos = Position;
        if (world.GetChunkByGlobalPosition(pos, out var chunk))
        {
            CurrentChunk = chunk;
            ChunkLocalPosition = (Vector3i)pos - chunk!.Position;
        }
        else
        {
            CurrentChunk = null;
        }


        if (IsGhostMode)
        {
            HandleGhostMode(elapsedSeconds);
        }
        else
        {
            HandleMovement(elapsedSeconds);
        }

        if (requestedRotation.X != 0 || requestedRotation.Y != 0 || requestedRotation.Z != 0)
        {
            var rotationPerSecond = (float)elapsedSeconds * RotationSpeed;
            var rotation = requestedRotation * rotationPerSecond;
            camera.AddRotation(rotation.X, rotation.Y, rotation.Z);
            Direction = camera.Front;
            requestedRotation = Vector3i.Zero;
            pickedBlock = world.PickBlock(camera!.Position, camera.Front);
            world.ChunkRenderer.PickedBlock = pickedBlock;
        }
    }

    /// <summary>
    /// If true, the player can walk through blocks and is not attached to the terrain.
    /// </summary>
    public bool IsGhostMode { get; set; } = false;

    public ICamera Camera => camera;

    /// <summary>
    /// Sets new player position and updates the camera position
    /// </summary>
    public Vector3 Position
    {
        get => position;
        set
        {
            position = value;
            UpdateCamera();
        }
    }

    public Vector3 Direction { get; set; }

    public Vector3 ChunkLocalPosition { get; set; } = new Vector3(0, 0, 0);

    public Chunk? CurrentChunk { get; set; } = null;

    public BlockState? CurrentBlockBellow { get; set; } = null;

    #region Commands
    public void AddRotation(float yawDegrees, float pitchDegrees, float rollDegrees)
    {
        requestedRotation = new Vector3(yawDegrees, pitchDegrees, rollDegrees);
    }

    public void MoveBack()
    {
        if (IsGhostMode || !isJumping)
            requestedMovement -= new Vector3(camera.Front.X, 0, camera.Front.Z);
    }

    public void MoveForward()
    {
        if (IsGhostMode || !isJumping)
            requestedMovement += new Vector3(camera.Front.X, 0, camera.Front.Z);
    }

    public void MoveLeft()
    {
        if (IsGhostMode || !isJumping)
            requestedMovement -= Vector3.Cross(Direction, Vector3.UnitY);
    }

    public void MoveRight()
    {
        if (IsGhostMode || !isJumping)
            requestedMovement += Vector3.Cross(Direction, Vector3.UnitY);
    }

    public void Jump()
    {
        if (isGrounded && !isJumping && !IsUpBlocked())
        {
            isGrounded = false;
            isJumping = true;

            //  initial velocity for the character to jump H high is the square root of 2 * H * g.
            //  H = 1.2f (that's a bit higher then a voxel block), g = 9.8f
            const float JumpVelocity = 4.85f;

            velocityY = JumpVelocity;
        }
    }

    public void BreakBlock()
    {
        if (pickedBlock is not null)
        {
            world.BreakBlock(pickedBlock.Value);
            pickedBlock = null;
            world.ChunkRenderer.PickedBlock = null;
        }
    }
    #endregion

    private void UpdateCamera()
    {
        var p = Position;
        p.Y += EyeHeight;
        p -= camera.Front.Normalized() * 0.2f;
        camera.Position = p;

        pickedBlock = world.PickBlock(camera!.Position, camera.Front);
        world.ChunkRenderer.PickedBlock = pickedBlock;
    }

    private void HandleMovement(double elapsedSeconds)
    {
        float terrainHeight = 0;
        var pos = Position;

        //  Verlet integration
        pos.Y += velocityY * (float)elapsedSeconds + 05f * Gravity * (float)elapsedSeconds * (float)elapsedSeconds;
        velocityY += (float)elapsedSeconds * Gravity;

        //  TOOD: make jumps based on physics formulas:
        //  * initial velocity for the character to jump H high is the square root of 2 * H * g.
        //  * height at any time during jump is y = 0.5gt² + v't + y' here v' and y' are starting velocity and position
        


        TravelXZ((float)elapsedSeconds * MovementSpeed, ref pos);

        var bellowBlock = world.GetBlockByPositionGlobalSafe((int)pos.X, (int)Math.Floor(pos.Y - 0.5f), (int)pos.Z);
        if (bellowBlock is null)
        {
            CurrentBlockBellow = null;
            isGrounded = false;
        }
        else
        {
            CurrentBlockBellow = bellowBlock;
            if (bellowBlock.Value.BlockType is not BlockType.None and not BlockType.WaterLevel)
            {
                terrainHeight = bellowBlock!.Value.LocalPosition.Y + 1;
            }
        }

        if (pos.Y <= terrainHeight + 0.01f)
        {
            pos.Y = terrainHeight;
            isJumping = false;
            isGrounded = true;
            velocityY = 0;
        }
        else if (pos.Y > terrainHeight && pos.Y < Position.Y)
        {
            isGrounded = false;
        }

        Position = pos;

        if (!isJumping)
        {
            requestedMovement.X = 0;
            requestedMovement.Z = 0;
        }
    }

    private void HandleGhostMode(double elapsedSeconds)
    {
        if (requestedMovement.Y != 0 || requestedMovement.Z != 0)
        {
            var pos = Position;
            var dir = requestedMovement.Normalized() * (float)elapsedSeconds * MovementSpeed * 10;

            pos.X += dir.X;
            pos.Z += dir.Z;

            requestedMovement.X = 0;
            requestedMovement.Z = 0;
            velocityY = 0;
            Position = pos;
        }
    }


    private void TravelXZ(float distance, ref Vector3 pos)
    {
        if (requestedMovement.X != 0 || requestedMovement.Z != 0)
        {
            var direction = requestedMovement.Normalized() * distance;
            const float r = 0.5f; // sphere radius

            var collider1 = world.PickBlock(pos + new Vector3(0, 0.25f, 0), direction, 0.25f);
            var collider2 = world.PickBlock(pos + new Vector3(0, 1.25f, 0), direction, 0.25f);
            if (collider1 is null && collider2 is null)
            {
                pos.X += direction.X;
                pos.Z += direction.Z;
            }
        }
    }

    private bool IsUpBlocked()
    {
        if (IsGhostMode)
            return false;

        if (CurrentChunk == null)
            return false;

        var c1 = position - bottomCornerOffsets[0];
        var c2 = position - bottomCornerOffsets[1];
        var c3 = position - bottomCornerOffsets[2];
        var c4 = position - bottomCornerOffsets[3];
        c1.Y += Height + 0.3f;
        c2.Y += Height + 0.3f;
        c3.Y += Height + 0.3f;
        c4.Y += Height + 0.3f;
        return HasBlockAbove(c1) || HasBlockAbove(c2) || HasBlockAbove(c3) || HasBlockAbove(c4);
    }

    //private bool IsDownBlocked()
    //{
    //    if (IsGhostMode)
    //        return false;

    //    if (CurrentChunk == null)
    //        return false;

    //    var c1 = position - bottomCornerOffsets[0];
    //    var c2 = position - bottomCornerOffsets[1];
    //    var c3 = position - bottomCornerOffsets[2];
    //    var c4 = position - bottomCornerOffsets[3];
    //    return HasBlockBellow(c1) || HasBlockBellow(c2) || HasBlockBellow(c3) || HasBlockBellow(c4);
    //}

    //private bool HasBlockBellow(Vector3 globalPosition)
    //{
    //    var chunkIndex = VoxelHelper.GetChunkIndexFromGlobalPosition(globalPosition);
    //    var chunk = world[chunkIndex];
    //    if (chunk is null) return false;

    //    var localPosition = globalPosition - chunk.Position;
    //    var block = chunk.GetTopBlockAtLocalXZ(localPosition);
    //    return block.BlockType is not BlockType.None and not BlockType.WaterLevel && MathF.Abs(block.LocalPosition.Y - localPosition.Y) < 1.25f;
    //}

    private bool HasBlockAbove(Vector3 globalPosition)
    {
        var chunkIndex = VoxelHelper.GetChunkIndexFromPositionGlobal(globalPosition);
        var chunk = world[chunkIndex];
        if (chunk is null) return false;

        var localPosition = globalPosition - chunk.Position;

        var block = chunk.GetBlockAtLocalPosition(localPosition);
        return block.BlockType is not BlockType.None and not BlockType.WaterLevel;
    }
}
