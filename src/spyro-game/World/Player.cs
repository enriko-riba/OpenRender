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

    private const float MovementSpeed = 2.0f;
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
            ChunkLocalPosition = Position - chunk!.Position;
            var height = chunk!.GetTerrainHeightAt((int)ChunkLocalPosition.X, (int)ChunkLocalPosition.Z);
            Position = new(Position.X, height + 3.1f, Position.Z);
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

    private float physicsAccumulator;

    public void Update(double elapsedSeconds, KeyboardState keyboardState)
    {
        // Fixed timestep accumulator (stable collisions irrespective of FPS)
        var frame = MathF.Min((float)elapsedSeconds, 0.25f);
        physicsAccumulator += frame;
        const float fixedDt = 1f / 60f;
        const int maxStepsPerFrame = 8;

        kbdActions.Update(keyboardState);

        var pos = Position;
        if (world.GetChunkByGlobalPosition(pos, out var chunk))
        {
            CurrentChunk = chunk;
            ChunkLocalPosition = pos - chunk!.Position;
        }
        else
        {
            CurrentChunk = null;
        }

        var steps = 0;
        while (physicsAccumulator >= fixedDt && steps < maxStepsPerFrame)
        {
            if (IsGhostMode)
                HandleGhostMode(fixedDt);
            else
                HandleMovement(fixedDt);

            physicsAccumulator -= fixedDt;
            steps++;
        }

        var hasMovement = requestedMovement.X != 0 || requestedMovement.Z != 0;
        var hasRotation = requestedRotation.X != 0 || requestedRotation.Y != 0 || requestedRotation.Z != 0;
        if (hasRotation)
        {
            var rotationPerSecond = (float)elapsedSeconds * RotationSpeed;
            var rotation = requestedRotation * rotationPerSecond;
            camera.AddRotation(rotation.X, rotation.Y, rotation.Z);
            Direction = camera.Front;
            requestedRotation = Vector3i.Zero;
        }

        if (hasRotation || hasMovement)
        {
            pickedBlock = world.PickBlock(camera.Position, Direction);
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

            // initial velocity to jump ~1.2 blocks high
            const float JumpVelocity = 5;// 4.85f;
            velocityY = JumpVelocity;
        }
    }

    public void ClimbingJump()
    {
        if (isGrounded && !isJumping && !IsUpBlocked())
        {
            isGrounded = false;
            isJumping = true;
            const float JumpVelocity = 4.50f;
            velocityY = JumpVelocity;
        }
    }

    public void BreakBlock()
    {
        if (pickedBlock is not null)
        {
            //Task.Run(() => world.BreakBlock(pickedBlock.Value));
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
        camera.Position = p;

        //pickedBlock = world.PickBlock(camera!.Position, camera.Front);
        world.ChunkRenderer.PickedBlock = pickedBlock;
    }

    private void HandleMovement(double elapsedSeconds)
    {
        float terrainHeight = 0;
        var pos = Position;

        // Verlet integration
        pos.Y += velocityY * (float)elapsedSeconds + 0.5f * Gravity * (float)elapsedSeconds * (float)elapsedSeconds;
        velocityY += (float)elapsedSeconds * Gravity;

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

        // Only clear per-frame input when not jumping, so jump preserves horizontal intent
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
            requestedMovement.Normalize();
            var dir = requestedMovement * (float)elapsedSeconds * MovementSpeed * 25;

            pos.X += dir.X;
            pos.Z += dir.Z;

            requestedMovement.X = 0;
            requestedMovement.Z = 0;
            velocityY = 0;
            Position = pos;
        }
    }

    private static Vector3 ReflectVector(Vector3 vector, Vector3 normal)
    {
        // Calculate the reflection using vector arithmetic
        return vector - 2 * Vector3.Dot(vector, normal) * normal;
    }

    private void TravelXZ(float distance, ref Vector3 pos)
    {
        if (requestedMovement.X != 0 || requestedMovement.Z != 0)
        {
            requestedMovement.Normalize();
            var direction = requestedMovement * distance;

            pos.X += direction.X;
            pos.Z += direction.Z;
            var currentBlock = world.GetBlockByPositionGlobalSafe((int)pos.X, (int)(pos.Y) + 1, (int)pos.Z);
            if (currentBlock is not null)
            {
                var neighbors = world.GetCollideCandidateBlocks(currentBlock.Value, 2);
                for (var i = 0; i < neighbors.Length - 1; i++)  //  ignore the last element which is block bellow
                {
                    //  order is front, left, right, back
                    var neighbor = neighbors[i];
                    if (neighbor is null || neighbor.Value.BlockType is BlockType.None or BlockType.WaterLevel)
                        continue;

                    const float r = 0.3f; // sphere radius
                    var collidingSphereCenter = pos + new Vector3(0, 1, 0);
                    var isCollision = VoxelWorld.IsSphereBlockCollision(neighbor.Value.Aabb, collidingSphereCenter, r);
                    if (isCollision)
                    {
                        var normal = (Vector3)currentBlock.Value.GlobalPosition - neighbor.Value.GlobalPosition;
                        //  check if direction is colliding with near 90 degrees angle
                        var dirNorm = direction.LengthSquared > 1e-6f ? direction.Normalized() : Vector3.Zero;
                        var dot = dirNorm == Vector3.Zero ? 0f : MathF.Abs(Vector3.Dot(normal, dirNorm));
                        if (dot > 0.85f)
                        {
                            // check if we can jump, must not be bellow a block nor a front block in eye level may exist
                            // the following picks a block in "front" of the players movement direction
                            var angle = (MathHelper.RadiansToDegrees((float)Math.Atan2(direction.X, direction.Z)) + 360) % 360;
                            var eyeLevelFrontBlock = angle switch
                            {
                                > 315f or <= 45f => neighbors[4],   //  front (south)
                                > 45f and <= 135f => neighbors[6],  //  right (east)
                                > 135f and <= 225f => neighbors[5], //  back (north)
                                _ => neighbors[7]                   //  left (west)
                            };
                            if (eyeLevelFrontBlock is null || eyeLevelFrontBlock.Value.BlockType == BlockType.None)
                            {
                                pos.Y += 0.05f;
                                ClimbingJump();
                                // preserve horizontal intent; do not break to allow separation below
                            }
                        }

                        // Robust sphere-AABB side resolution in XZ to prevent penetration
                        var aabb = neighbor.Value.Aabb;
                        var closestX = Math.Clamp(collidingSphereCenter.X, aabb.Min.X, aabb.Max.X);
                        var closestZ = Math.Clamp(collidingSphereCenter.Z, aabb.Min.Z, aabb.Max.Z);
                        var nx = collidingSphereCenter.X - closestX;
                        var nz = collidingSphereCenter.Z - closestZ;
                        var len = MathF.Sqrt(nx * nx + nz * nz);
                        const float epsSep = 0.001f;
                        Vector2 nXZ;
                        if (len < 1e-4f)
                        {
                            // Fallback: choose nearest face outward normal
                            var dLeft  = MathF.Abs(collidingSphereCenter.X - aabb.Min.X);
                            var dRight = MathF.Abs(aabb.Max.X - collidingSphereCenter.X);
                            var dFront = MathF.Abs(collidingSphereCenter.Z - aabb.Min.Z);
                            var dBack  = MathF.Abs(aabb.Max.Z - collidingSphereCenter.Z);
                            var minD = MathF.Min(MathF.Min(dLeft, dRight), MathF.Min(dFront, dBack));
                            if (minD == dLeft)      nXZ = new Vector2(-1, 0);
                            else if (minD == dRight)nXZ = new Vector2( 1, 0);
                            else if (minD == dFront)nXZ = new Vector2( 0,-1);
                            else                    nXZ = new Vector2( 0, 1);
                            len = 1f;
                        }
                        else
                        {
                            nXZ = new Vector2(nx / len, nz / len);
                        }
                        var penetration = r - len;
                        if (penetration > 0)
                        {
                            pos.X += nXZ.X * (penetration + epsSep);
                            pos.Z += nXZ.Y * (penetration + epsSep);
                        }
                        // Remove into-normal component of this frame's horizontal move (sliding)
                        var moveDot = direction.X * nXZ.X + direction.Z * nXZ.Y;
                        if (moveDot > 0)
                        {
                            pos.X -= nXZ.X * moveDot;
                            pos.Z -= nXZ.Y * moveDot;
                        }
                        // do not break; allow resolving multiple contacts
                    }
                }
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
        if (localPosition.Y is < 0 or >= VoxelHelper.ChunkYSize)
        {
            return false;
        }
        var block = chunk.GetBlockAtLocalPosition(localPosition);
        return block.BlockType is not BlockType.None and not BlockType.WaterLevel;
    }

    // helper removed
}
