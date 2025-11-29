using OpenRender;
using OpenRender.Core.Rendering;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SpyroGame.Input;

namespace SpyroGame.World;

public class Player
{
    private const float HalfWidth = 0.3f; // Reduced slightly for better fit
    private const float Height = 1.7f;
    private const float EyeHeight = 1.6f;
    private const float Gravity = -20.0f; // Stronger gravity for snappier feel

    private const float MoveSpeed = 6.0f;
    private const float JumpForce = 8.5f;
    private const float Friction = 6.0f;
    private const float AirControl = 0.2f;
    private const float RotationSpeed = 10;

    private static readonly Vector3[] bottomCornerOffsets = [
        new Vector3(-HalfWidth, 0, -HalfWidth), // northwest
        new Vector3(-HalfWidth, 0, +HalfWidth), // southwest
        new Vector3(+HalfWidth, 0, -HalfWidth), // northeast
        new Vector3(+HalfWidth, 0, +HalfWidth), // southeast
    ];

    private readonly ICamera camera;
    private readonly VoxelWorld world;
    private ChunkStreamingManager? streamingManager;
    private Vector3 position;

    public ChunkStreamingManager? StreamingManager
    {
        get => streamingManager;
        set => streamingManager = value;
    }

    private bool isGrounded;
    private Vector3 velocity;
    private Vector3 requestedMovement;
    private Vector3 requestedRotation;
    private BlockState? pickedBlock = null;
    private readonly KeyboardActionMapper kbdActions = new();

    // speed modifiers captured each Update() from KeyboardState
    private bool _isSprinting, _isCrouching;

    public Player(ICamera camera, Vector3 position, VoxelWorld world, ChunkStreamingManager? streamingManager = null)
    {
        this.camera = camera;
        this.world = world;
        this.streamingManager = streamingManager;
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

    internal bool IsGrounded => isGrounded;
    internal bool IsJumping => !isGrounded && velocity.Y > 0;
    internal float VelocityY => velocity.Y;
    internal Vector3 Velocity => velocity;
    internal Vector3 RequestedMovement => requestedMovement;
    internal BlockState? PickedBlock
    {
        get => pickedBlock;
        set => pickedBlock = value;
    }

    public void Update(double elapsedSeconds, KeyboardState keyboardState, MouseState mouseState)
    {
        // Update actions (e.g., toggle ghost mode)
        kbdActions.Update(keyboardState);

        // Sample speed mods before processing
        _isSprinting = keyboardState.IsKeyDown(Keys.LeftShift) || keyboardState.IsKeyDown(Keys.RightShift);
        _isCrouching = keyboardState.IsKeyDown(Keys.LeftControl) || keyboardState.IsKeyDown(Keys.RightControl);

        // Process movement/physics (grounded or ghost)
        if (IsGhostMode)
        {
            HandleGhostMode(elapsedSeconds);
        }
        else
        {
            HandleMovement(elapsedSeconds);
        }

        // Update camera position and direction
        UpdateCamera();

        // Handle rotation
        if (requestedRotation != Vector3.Zero)
        {
            camera.AddRotation(requestedRotation.X * RotationSpeed, requestedRotation.Y * RotationSpeed, requestedRotation.Z * RotationSpeed);
            Direction = camera.Front;
            requestedRotation = Vector3.Zero;
        }

        // Update chunk tracking
        if (world.GetChunkByGlobalPosition(Position, out var chunk))
        {
            CurrentChunk = chunk;
            ChunkLocalPosition = Position - chunk!.Position;
        }

        // CPU raycast for picked block - will be replaced by GPU picking in GameScene
        pickedBlock = world.PickBlock(camera.Position, camera.Front);

        // Handle block breaking
        if (mouseState.IsButtonPressed(MouseButton.Left))
        {
            BreakBlock();
        }
    }

    /// <summary>
    /// If true, the player can walk through blocks and is not attached to the terrain.
    /// Reset vertical dynamics when toggled to avoid stale velocities.
    /// </summary>
    private bool _isGhostMode;
    public bool IsGhostMode
    {
        get => _isGhostMode;
        set
        {
            if (_isGhostMode == value) return;
            _isGhostMode = value;
            velocity = Vector3.Zero;
            isGrounded = false;
        }
    }

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
        => requestedRotation = new Vector3(yawDegrees, pitchDegrees, rollDegrees);

    // Input layer always records intent; physics layer decides how much applies (air control).
    public void MoveBack() => requestedMovement -= new Vector3(camera.Front.X, 0, camera.Front.Z);
    public void MoveForward() => requestedMovement += new Vector3(camera.Front.X, 0, camera.Front.Z);
    public void MoveLeft() => requestedMovement -= Vector3.Cross(Direction, Vector3.UnitY);
    public void MoveRight() => requestedMovement += Vector3.Cross(Direction, Vector3.UnitY);

    public void Jump()
    {
        // Prevent initiating a jump when inside a low tunnel (headroom < ~1 block)
        if (isGrounded && !IsUpBlocked() && !IsInLowHeadroomTunnel())
        {
            isGrounded = false;
            velocity.Y = JumpForce;
        }
    }

    public void ClimbingJump()
    {
        if (isGrounded && !IsUpBlocked() && !IsInLowHeadroomTunnel())
        {
            isGrounded = false;
            velocity.Y = JumpForce;
        }
    }

    public void BreakBlock()
    {
        if (pickedBlock is not null)
        {
            if (streamingManager != null)
            {
                Log.Info($"Player breaking block at {pickedBlock.Value.GlobalPosition}");
                streamingManager.ApplyBlockEdit(pickedBlock.Value.GlobalPosition, BlockType.None, true);
            }
            else
            {
                Log.Error("Player.BreakBlock: streamingManager is null!");
            }

            pickedBlock = null;
            // NOTE: PickedBlock sync handled by GameScene
        }
        else
        {
            Log.Info("Player.BreakBlock: pickedBlock is null");
        }
    }
    #endregion

    private void UpdateCamera()
    {
        var p = Position;
        p.Y += EyeHeight;
        camera.Position = p;
    }

    private void HandleGhostMode(double elapsedSeconds)
    {
        velocity = Vector3.Zero;
        if (requestedMovement.LengthSquared > 0)
        {
            var dir = requestedMovement.Normalized() * (float)elapsedSeconds * MoveSpeed * 4.0f;
            position += dir;
            requestedMovement = Vector3.Zero;
        }
    }

    private void HandleMovement(double elapsedSeconds)
    {
        var dt = (float)elapsedSeconds;

        // Apply gravity
        velocity.Y += Gravity * dt;

        // Calculate wish direction
        var wishDir = Vector3.Zero;
        if (requestedMovement.LengthSquared > 0.001f)
        {
            wishDir = requestedMovement.Normalized();
        }

        var speed = MoveSpeed;
        if (_isSprinting) speed *= 1.5f;
        if (_isCrouching) speed *= 0.5f;

        if (isGrounded)
        {
            // Instant velocity change (Infinite friction)
            velocity.X = wishDir.X * speed;
            velocity.Z = wishDir.Z * speed;
        }
        // No air control: velocity is preserved (inertial)
        
        // Move
        Move(velocity * dt);

        // Clamp horizontal velocity
        var maxSpeed = _isSprinting ? MoveSpeed * 1.5f : (_isCrouching ? MoveSpeed * 0.5f : MoveSpeed);
        var hVel = new Vector2(velocity.X, velocity.Z);
        if (hVel.LengthSquared > maxSpeed * maxSpeed)
        {
            hVel = hVel.Normalized() * maxSpeed;
            velocity.X = hVel.X;
            velocity.Z = hVel.Y;
        }

        // Check ground
        CheckGround();

        // Reset requested movement
        requestedMovement = Vector3.Zero;
    }

    private void Accelerate(Vector3 wishDir, float wishSpeed, float accel, float dt)
    {
        var currentSpeed = Vector3.Dot(velocity, wishDir);
        var addSpeed = wishSpeed - currentSpeed;
        if (addSpeed <= 0) return;

        var accelSpeed = accel * dt * wishSpeed;
        if (accelSpeed > addSpeed) accelSpeed = addSpeed;

        velocity.X += accelSpeed * wishDir.X;
        velocity.Z += accelSpeed * wishDir.Z;
    }

    private void Move(Vector3 delta)
    {
        // Separate XZ and Y movement for stability
        var deltaXZ = new Vector3(delta.X, 0, delta.Z);
        var deltaY = new Vector3(0, delta.Y, 0);

        // Move XZ
        position += deltaXZ;
        ResolveCollisionXZ();

        // Move Y
        position += deltaY;
        ResolveCollisionY();
    }

    private void ResolveCollisionXZ()
    {
        var currentBlock = world.GetBlockByPositionGlobalSafe((int)position.X, (int)position.Y, (int)position.Z);
        if (currentBlock == null) return;

        // Check 3 blocks high to ensure head (at ~1.7m) is covered even if standing on a slab/edge
        var neighbors = world.GetCollideCandidateBlocks(currentBlock.Value, 3);
        foreach (var neighbor in neighbors)
        {
            if (neighbor == null || neighbor.Value.BlockType == BlockType.None || neighbor.Value.BlockType == BlockType.WaterLevel) continue;

            var (Min, Max) = neighbor.Value.Aabb;

            // Check vertical overlap (Capsule height)
            if (position.Y >= Max.Y || position.Y + Height <= Min.Y) continue;

            // Closest point on AABB to cylinder axis
            var closestX = Math.Clamp(position.X, Min.X, Max.X);
            var closestZ = Math.Clamp(position.Z, Min.Z, Max.Z);

            var dx = position.X - closestX;
            var dz = position.Z - closestZ;
            var distSq = dx * dx + dz * dz;

            if (distSq < HalfWidth * HalfWidth)
            {
                var dist = MathF.Sqrt(distSq);
                var penetration = HalfWidth - dist;

                Vector3 normal;
                if (dist < 1e-4f)
                {
                    // Fallback normal
                    normal = Vector3.Normalize(new Vector3(position.X - (Min.X + Max.X) * 0.5f, 0, position.Z - (Min.Z + Max.Z) * 0.5f));
                    if (normal.LengthSquared < 0.1f) normal = Vector3.UnitX;
                }
                else
                {
                    normal = new Vector3(dx / dist, 0, dz / dist);
                }

                // Auto-jump check
                var autoJumpTriggered = false;
                if (isGrounded)
                {
                    autoJumpTriggered = CheckAutoJump(neighbor.Value, normal);
                }
                else
                {
                    // Airborne collision logic
                    var obstacleTop = Max.Y;
                    var stepHeight = obstacleTop - position.Y;

                    // Allow penetration for 1-block high obstacles while jumping
                    if (stepHeight <= 1.1f && velocity.Y > 0)
                    {
                        continue; // Ignore collision
                    }
                    
                    // Stop horizontal movement on collision if not a step-able block
                    // Only if moving into the wall
                    if (Vector3.Dot(velocity, normal) < 0)
                    {
                        // If falling, stop completely to prevent head clipping issues
                        if (velocity.Y < 0)
                        {
                             velocity.X = 0;
                             velocity.Z = 0;
                        }
                        else
                        {
                            // Slide instead of stop to allow movement along walls while jumping/rising
                            var dot = Vector3.Dot(velocity, normal);
                            velocity -= normal * dot;
                        }
                    }
                }

                // Push out (skip if auto-jump triggered to allow smooth transition)
                if (!autoJumpTriggered)
                {
                    position += normal * (penetration + 0.001f);

                    // Slide velocity (only if grounded)
                    if (isGrounded)
                    {
                        var dot = Vector3.Dot(velocity, normal);
                        if (dot < 0)
                        {
                            velocity -= normal * dot;
                        }
                    }
                }
            }
        }
    }

    private void ResolveCollisionY()
    {
        // Ceiling check
        if (velocity.Y > 0)
        {
            if (IsHeadHittingCeiling())
            {
                velocity.Y = 0;
                // Push down slightly to avoid sticking
                var ceilingY = MathF.Floor(position.Y + Height);
                position.Y = ceilingY - Height - 0.001f;
            }
        }
        
        // Ground collision is handled by CheckGround mostly, but we need to stop falling if we hit something
        // Actually, CheckGround handles the "landing" part.
        // But if we are moving up and hit a ceiling, we stop.
        // If we are moving down, CheckGround will snap us.
    }

    private bool IsHeadHittingCeiling()
    {
        if (IsGhostMode) return false;

        var topY = position.Y + Height;
        var checkRadius = HalfWidth - 0.05f; // Slightly smaller to avoid wall friction

        var offsets = new[]
        {
            new Vector3(-checkRadius, 0, -checkRadius),
            new Vector3(-checkRadius, 0, +checkRadius),
            new Vector3(+checkRadius, 0, -checkRadius),
            new Vector3(+checkRadius, 0, +checkRadius)
        };

        foreach (var off in offsets)
        {
            var p = position + off;
            var block = world.GetBlockByPositionGlobalSafe((int)p.X, (int)topY, (int)p.Z);
            if (block != null && block.Value.BlockType != BlockType.None && block.Value.BlockType != BlockType.WaterLevel)
                return true;
        }
        return false;
    }

    private bool CheckAutoJump(BlockState obstacle, Vector3 wallNormal)
    {
        if (!isGrounded) return false; // Only auto-jump from ground

        // Check height
        var obstacleTop = obstacle.Aabb.Max.Y;
        var stepHeight = obstacleTop - position.Y;

        if (stepHeight is > 0 and <= 1.1f)
        {
            // Check if the obstacle itself is blocked above (wall > 1 block high)
            var blockAbove = world.GetBlockByPositionGlobalSafe((int)obstacle.GlobalPosition.X, (int)obstacle.GlobalPosition.Y + 1, (int)obstacle.GlobalPosition.Z);
            if (blockAbove != null && blockAbove.Value.BlockType != BlockType.None && blockAbove.Value.BlockType != BlockType.WaterLevel)
            {
                return false;
            }

            // Check clearance above obstacle at the landing spot
            // We project where we would land
            var moveDir = new Vector3(velocity.X, 0, velocity.Z).Normalized();
            var landPos = position + moveDir * 0.5f; // Look slightly ahead
            landPos.Y = obstacleTop + 0.01f;

            // Check for headroom at landing position
            // We check a box of player size at landPos
            if (IsBoxBlocked(landPos))
            {
                return false;
            }
            
            // Angle check
            var dot = Vector3.Dot(moveDir, -wallNormal);
            
            if (dot > 0.7f)
            {
                // Trigger auto-jump with velocity impulse
                // v = sqrt(2 * g * h)
                var jumpHeight = stepHeight + 0.2f; // Clear the edge
                var jumpVel = MathF.Sqrt(2 * MathF.Abs(Gravity) * jumpHeight);

                velocity.Y = jumpVel;
                isGrounded = false;
                return true;
            }
        }
        return false;
    }

    private bool IsBoxBlocked(Vector3 pos)
    {
        var minX = (int)MathF.Floor(pos.X - HalfWidth + 0.1f);
        var maxX = (int)MathF.Floor(pos.X + HalfWidth - 0.1f);
        var minZ = (int)MathF.Floor(pos.Z - HalfWidth + 0.1f);
        var maxZ = (int)MathF.Floor(pos.Z + HalfWidth - 0.1f);
        var minY = (int)MathF.Floor(pos.Y + 0.1f);
        var maxY = (int)MathF.Floor(pos.Y + Height - 0.1f);

        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                for (var z = minZ; z <= maxZ; z++)
                {
                    var b = world.GetBlockByPositionGlobalSafe(x, y, z);
                    if (b != null && b.Value.BlockType != BlockType.None && b.Value.BlockType != BlockType.WaterLevel)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private void CheckGround()
    {
        // Shape cast downwards
        // For simplicity, check blocks around feet

        var minX = (int)MathF.Floor(position.X - HalfWidth + 0.1f);
        var maxX = (int)MathF.Floor(position.X + HalfWidth - 0.1f);
        var minZ = (int)MathF.Floor(position.Z - HalfWidth + 0.1f);
        var maxZ = (int)MathF.Floor(position.Z + HalfWidth - 0.1f);
        var y = (int)MathF.Floor(position.Y - 0.1f); // Check block below feet

        var maxY = -float.MaxValue;
        var foundGround = false;

        for (var x = minX; x <= maxX; x++)
        {
            for (var z = minZ; z <= maxZ; z++)
            {
                var block = world.GetBlockByPositionGlobalSafe(x, y, z);
                if (block != null && block.Value.BlockType != BlockType.None && block.Value.BlockType != BlockType.WaterLevel)
                {
                    if (block.Value.Aabb.Max.Y > maxY)
                    {
                        maxY = block.Value.Aabb.Max.Y;
                        foundGround = true;
                        CurrentBlockBellow = block;
                    }
                }
            }
        }

        if (foundGround)
        {
            // Snap to ground if close enough (Step Down) or if we were already grounded/falling slightly
            // Allow snapping down up to 1.1m (step height) to handle stairs smoothly
            var snapDist = 1.1f;
            
            if (position.Y <= maxY + snapDist && velocity.Y <= 0)
            {
                position.Y = maxY;
                velocity.Y = 0;
                
                if (!isGrounded)
                {
                    // Landing impact: dampen horizontal velocity
                    velocity.X *= 0.5f;
                    velocity.Z *= 0.5f;
                }
                
                isGrounded = true;
            }
            else
            {
                isGrounded = false;
            }
        }
        else
        {
            isGrounded = false;
            CurrentBlockBellow = null;
        }
    }

    private bool IsUpBlocked()
    {
        if (IsGhostMode) return false;
        if (CurrentChunk == null) return false;

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

    // Returns true if the vertical clearance above the player's head is less than ~1 block
    // (e.g., inside a 1-block-high tunnel). In that case, suppress jump to avoid head penetration.
    private bool IsInLowHeadroomTunnel()
    {
        if (IsGhostMode) return false;
        // Evaluate at the current position using the player horizontal footprint
        const float eps = 0.001f;
        var headTop = position.Y + Height - 0.05f;
        var tileY = (int)MathF.Floor(headTop) + 1; // immediate block above head tile
        var minX = (int)MathF.Floor(position.X - HalfWidth + eps);
        var maxX = (int)MathF.Floor(position.X + HalfWidth - eps);
        var minZ = (int)MathF.Floor(position.Z - HalfWidth + eps);
        var maxZ = (int)MathF.Floor(position.Z + HalfWidth - eps);

        for (var tz = minZ; tz <= maxZ; tz++)
            for (var tx = minX; tx <= maxX; tx++)
            {
                var b = world.GetBlockByPositionGlobalSafe(tx, tileY, tz);
                if (b is not null && b.Value.BlockType is not BlockType.None and not BlockType.WaterLevel)
                    return true; // ceiling within one block above head
            }
        return false;
    }

    private bool HasBlockAbove(Vector3 globalPosition)
    {
        var x = (int)globalPosition.X;
        var y = (int)globalPosition.Y;
        var z = (int)globalPosition.Z;
        
        var block = world.GetBlockByPositionGlobalSafe(x, y, z);
        return block is not null && block.Value.BlockType is not BlockType.None and not BlockType.WaterLevel;
    }
}
