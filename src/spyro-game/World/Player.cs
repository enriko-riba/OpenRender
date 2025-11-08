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

    // speed modifiers captured each Update() from KeyboardState
    private bool _isSprinting, _isCrouching;

    // inertial carry for ballistic mid-air motion
    private Vector2 inertialStepXZ;

    // step-up smoothing
    private float stepUpGrace;      // seconds: ignore ground snap while rising after auto-step
    private float stepUpCooldown;   // seconds: prevent retrigger spam

    // tuned to reduce “gluing”
    private const float StepUpGraceDuration = 0.18f;   // was 0.12f
    private const float StepUpCooldownDuration = 0.08f;
    private const float StepUpImpulse = 3.6f;          // was 3.2f

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

        // capture sprint/crouch modifiers each frame
        _isSprinting = keyboardState.IsKeyDown(Keys.LeftShift) || keyboardState.IsKeyDown(Keys.RightShift);
        _isCrouching = keyboardState.IsKeyDown(Keys.LeftControl) || keyboardState.IsKeyDown(Keys.RightControl);

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
            // decrement step timers per fixed step
            stepUpGrace = MathF.Max(0f, stepUpGrace - fixedDt);
            stepUpCooldown = MathF.Max(0f, stepUpCooldown - fixedDt);

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
    /// Reset vertical dynamics when toggled to avoid stale velocities.
    /// </summary>
    private bool _isGhostMode = true;
    public bool IsGhostMode
    {
        get => _isGhostMode;
        set
        {
            if (_isGhostMode == value) return;
            _isGhostMode = value;
            velocityY = 0f;
            isJumping = false;
            isGrounded = false;
            inertialStepXZ = Vector2.Zero;
            stepUpGrace = 0f;
            stepUpCooldown = 0f;
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
    {
        requestedRotation = new Vector3(yawDegrees, pitchDegrees, rollDegrees);
    }

    // Input layer always records intent; physics layer decides how much applies (air control).
    public void MoveBack() => requestedMovement -= new Vector3(camera.Front.X, 0, camera.Front.Z);
    public void MoveForward() => requestedMovement += new Vector3(camera.Front.X, 0, camera.Front.Z);
    public void MoveLeft() => requestedMovement -= Vector3.Cross(Direction, Vector3.UnitY);
    public void MoveRight() => requestedMovement += Vector3.Cross(Direction, Vector3.UnitY);

    public void Jump()
    {
        // Prevent initiating a jump when inside a low tunnel (headroom < ~1 block)
        if (isGrounded && !isJumping && !IsUpBlocked() && !IsInLowHeadroomTunnel())
        {
            isGrounded = false;
            isJumping = true;

            const float JumpVelocity = 5f; // ~1.2 blocks
            velocityY = JumpVelocity;
        }
    }

    public void ClimbingJump()
    {
        if (isGrounded && !isJumping && !IsUpBlocked() && !IsInLowHeadroomTunnel())
        {
            isGrounded = false;
            isJumping = true;
            const float JumpVelocity = 5.0f;// 4.50f;
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
        camera.Position = p;

        world.ChunkRenderer.PickedBlock = pickedBlock;
    }

    private void HandleMovement(double elapsedSeconds)
    {
        float terrainHeight = float.NaN;
        var pos = Position;

        // integrate Y
        var dy = velocityY * (float)elapsedSeconds + 0.5f * Gravity * (float)elapsedSeconds * (float)elapsedSeconds;
        pos.Y += dy;
        velocityY += (float)elapsedSeconds * Gravity;

        // prevent head penetration into ceiling: if moving up and head space blocked, clamp and zero vertical velocity
        if (dy > 0f && !IsGhostMode)
        {
            // Robust head clamp: AABB test at head-top tile across horizontal extents
            const float eps = 0.001f;
            var headTop = pos.Y + Height - 0.05f;
            var tileY = (int)MathF.Floor(headTop);
            int minX = (int)MathF.Floor(pos.X - HalfWidth + eps);
            int maxX = (int)MathF.Floor(pos.X + HalfWidth - eps);
            int minZ = (int)MathF.Floor(pos.Z - HalfWidth + eps);
            int maxZ = (int)MathF.Floor(pos.Z + HalfWidth - eps);
            bool blocked = false;
            for (int tz = minZ; tz <= maxZ && !blocked; tz++)
                for (int tx = minX; tx <= maxX && !blocked; tx++)
                {
                    var b = world.GetBlockByPositionGlobalSafe(tx, tileY, tz);
                    if (b is not null && b.Value.BlockType is not BlockType.None and not BlockType.WaterLevel)
                        blocked = true;
                }
            if (blocked)
            {
                pos.Y = tileY - Height + 0.001f;
                velocityY = 0f;
                isJumping = false;
            }
        }

        // speed tiers
        var tier = _isSprinting ? 1.5f : _isCrouching ? 0.6f : 1.0f;

        // compute this-step ground wish (displacement), but only apply when grounded
        var groundStep = Vector3.Zero;
        if (requestedMovement.X != 0 || requestedMovement.Z != 0)
        {
            var wish = requestedMovement.Normalized();
            groundStep = new Vector3(wish.X, 0, wish.Z) * (float)elapsedSeconds * MovementSpeed * tier;
        }

        // if grounded: apply ground step and latch inertial (for future air)
        // if airborne: ignore new input and apply latched inertial step instead
        const float eps2 = 1e-8f;
        if (isGrounded)
        {
            if (groundStep.LengthSquared > eps2)
            {
                inertialStepXZ = new Vector2(groundStep.X, groundStep.Z);
                TravelXZStep(ref pos, groundStep);
            }
            else
            {
                inertialStepXZ = Vector2.Zero;
            }
        }
        else
        {
            if (inertialStepXZ.LengthSquared > eps2)
            {
                // optional tiny drag (comment out if you want perfect inertia)
                inertialStepXZ *= 0.995f;
                var airStep = new Vector3(inertialStepXZ.X, 0, inertialStepXZ.Y);
                TravelXZStep(ref pos, airStep);
            }
            else
            {
                inertialStepXZ = Vector2.Zero;
            }
        }

        // Ground detection via local voxel query (respects caves/tunnels)
        var bellowBlock = world.GetBlockByPositionGlobalSafe((int)pos.X, (int)Math.Floor(pos.Y - 0.5f), (int)pos.Z);
        if (bellowBlock is null)
        {
            CurrentBlockBellow = null;
            // If GPU height not available, remain airborne until we have a solid reference
            isGrounded = false;
        }
        else
        {
            CurrentBlockBellow = bellowBlock;
            if (bellowBlock.Value.BlockType is not BlockType.None and not BlockType.WaterLevel)
                terrainHeight = bellowBlock.Value.GlobalPosition.Y + 1f;
        }

        // During step-up grace, completely skip snapping logic to avoid “gluing”
        if (stepUpGrace > 0f)
        {
            isGrounded = false; // remain airborne through grace window
        }
        else
        {
            // respect ascent: only snap when grace expired AND not ascending
            var ascending = velocityY > 0f;
            var allowSnap = stepUpGrace <= 0f && !ascending;  // <-- stricter than before

            if (!float.IsNaN(terrainHeight) && allowSnap && pos.Y <= terrainHeight + 0.01f)
            {
                pos.Y = terrainHeight + 0.001f;
                isJumping = false;
                isGrounded = true;
                velocityY = 0;
                // keep inertialStepXZ as last applied ground step for next takeoff
            }
            else if (!float.IsNaN(terrainHeight) && pos.Y > terrainHeight && pos.Y < Position.Y)
            {
                isGrounded = false;
            }
        }

        Position = pos;

        // clear per-step intent; inputs repopulate each frame
        requestedMovement.X = 0;
        requestedMovement.Z = 0;
    }

    private void TravelXZStep(ref Vector3 pos, Vector3 step)
    {
        if (step.X == 0 && step.Z == 0) return;

        // proposed move
        pos.X += step.X;
        pos.Z += step.Z;

        var currentBlock = world.GetBlockByPositionGlobalSafe((int)pos.X, (int)(pos.Y) + 1, (int)pos.Z);
        if (currentBlock is null) return;

        var neighbors = world.GetCollideCandidateBlocks(currentBlock.Value, 2);
        for (var i = 0; i < neighbors.Length - 1; i++)
        {
            var neighbor = neighbors[i];
            if (neighbor is null || neighbor.Value.BlockType is BlockType.None or BlockType.WaterLevel) continue;

            const float r = 0.3f;
            var collidingSphereCenter = pos + new Vector3(0, 1, 0);
            if (!VoxelWorld.IsSphereBlockCollision(neighbor.Value.Aabb, collidingSphereCenter, r)) continue;

            // normalized world-space normal between blocks for the steep-angle test
            var raw = (Vector3)currentBlock.Value.GlobalPosition - neighbor.Value.GlobalPosition;
            var nWorld = raw.LengthSquared > 1e-6f ? Vector3.Normalize(raw) : Vector3.UnitZ;

            var dirNorm = step.LengthSquared > 1e-6f ? Vector3.Normalize(step) : Vector3.Zero;
            var dot = dirNorm == Vector3.Zero ? 0f : MathF.Abs(Vector3.Dot(nWorld, dirNorm));
            if (dot > 0.85f && isGrounded) // only auto-step from ground
            {
                var angle = (MathHelper.RadiansToDegrees((float)Math.Atan2(step.X, step.Z)) + 360) % 360;
                var eyeLevelFrontBlock = angle switch
                {
                    > 315f or <= 45f => neighbors[4],   // front (south)
                    > 45f and <= 135f => neighbors[6],  // right (east)
                    > 135f and <= 225f => neighbors[5], // back (north)
                    _ => neighbors[7]                   // left (west)
                };
                if (eyeLevelFrontBlock is null || eyeLevelFrontBlock.Value.BlockType == BlockType.None)
                {
                    // gentle auto-step: single impulse + grace, no sticky snap
                    if (stepUpCooldown <= 0f)
                    {
                        pos.Y += 0.02f; // tiny pre-lift to avoid immediate re-collide (reduced)
                        isGrounded = false;
                        isJumping = true;
                        velocityY = MathF.Max(velocityY, StepUpImpulse);
                        stepUpGrace = StepUpGraceDuration;
                        stepUpCooldown = StepUpCooldownDuration;
                    }
                }
            }

            // side resolution & sliding (same as before, using 'step')
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
                var dLeft = MathF.Abs(collidingSphereCenter.X - aabb.Min.X);
                var dRight = MathF.Abs(aabb.Max.X - collidingSphereCenter.X);
                var dFront = MathF.Abs(collidingSphereCenter.Z - aabb.Min.Z);
                var dBack = MathF.Abs(aabb.Max.Z - collidingSphereCenter.Z);
                var minD = MathF.Min(MathF.Min(dLeft, dRight), MathF.Min(dFront, dBack));
                if (minD == dLeft) nXZ = new Vector2(-1, 0);
                else if (minD == dRight) nXZ = new Vector2(1, 0);
                else if (minD == dFront) nXZ = new Vector2(0, -1);
                else nXZ = new Vector2(0, 1);
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

            var moveDot = step.X * nXZ.X + step.Z * nXZ.Y;
            if (moveDot > 0)
            {
                pos.X -= nXZ.X * moveDot;
                pos.Z -= nXZ.Y * moveDot;
            }
        }
    }


    private void HandleGhostMode(double elapsedSeconds)
    {
        // ensure no vertical drift persists in ghost
        velocityY = 0f;

        // BUGFIX: check X/Z, not Y/Z
        if (requestedMovement.X != 0 || requestedMovement.Z != 0)
        {
            var pos = Position;
            requestedMovement.Normalize();
            var dir = requestedMovement * (float)elapsedSeconds * MovementSpeed * 25f;

            pos.X += dir.X;
            pos.Z += dir.Z;

            requestedMovement.X = 0;
            requestedMovement.Z = 0;
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
                for (var i = 0; i < neighbors.Length - 1; i++)  // ignore last (below)
                {
                    var neighbor = neighbors[i];
                    if (neighbor is null || neighbor.Value.BlockType is BlockType.None or BlockType.WaterLevel)
                        continue;

                    const float r = 0.3f; // sphere radius
                    var collidingSphereCenter = pos + new Vector3(0, 1, 0);
                    var isCollision = VoxelWorld.IsSphereBlockCollision(neighbor.Value.Aabb, collidingSphereCenter, r);
                    if (isCollision)
                    {
                        // Normalized world-space separating hint between blocks (normalize for angle test)
                        var raw = (Vector3)currentBlock.Value.GlobalPosition - neighbor.Value.GlobalPosition;
                        var nWorld = raw.LengthSquared > 1e-6f ? Vector3.Normalize(raw) : Vector3.UnitZ;

                        //  check if direction is near head-on to trigger climb
                        var dirNorm = direction.LengthSquared > 1e-6f ? Vector3.Normalize(direction) : Vector3.Zero;
                        var dot = dirNorm == Vector3.Zero ? 0f : MathF.Abs(Vector3.Dot(nWorld, dirNorm));
                        if (dot > 0.85f)
                        {
                            // pick front block at eye level in movement direction
                            var angle = (MathHelper.RadiansToDegrees((float)Math.Atan2(direction.X, direction.Z)) + 360) % 360;
                            var eyeLevelFrontBlock = angle switch
                            {
                                > 315f or <= 45f => neighbors[4],   // front (south)
                                > 45f and <= 135f => neighbors[6],  // right (east)
                                > 135f and <= 225f => neighbors[5], // back (north)
                                _ => neighbors[7]                   // left (west)
                            };
                            if (eyeLevelFrontBlock is null || eyeLevelFrontBlock.Value.BlockType == BlockType.None)
                            {
                                pos.Y += 0.05f;
                                ClimbingJump();
                                // preserve horizontal intent; continue resolving
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
                            var dLeft = MathF.Abs(collidingSphereCenter.X - aabb.Min.X);
                            var dRight = MathF.Abs(aabb.Max.X - collidingSphereCenter.X);
                            var dFront = MathF.Abs(collidingSphereCenter.Z - aabb.Min.Z);
                            var dBack = MathF.Abs(aabb.Max.Z - collidingSphereCenter.Z);
                            var minD = MathF.Min(MathF.Min(dLeft, dRight), MathF.Min(dFront, dBack));
                            if (minD == dLeft) nXZ = new Vector2(-1, 0);
                            else if (minD == dRight) nXZ = new Vector2(1, 0);
                            else if (minD == dFront) nXZ = new Vector2(0, -1);
                            else nXZ = new Vector2(0, 1);
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
                        // slide: remove into-normal component of horizontal move
                        var moveDot = direction.X * nXZ.X + direction.Z * nXZ.Y;
                        if (moveDot > 0)
                        {
                            pos.X -= nXZ.X * moveDot;
                            pos.Z -= nXZ.Y * moveDot;
                        }
                    }
                }
            }
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
        int minX = (int)MathF.Floor(position.X - HalfWidth + eps);
        int maxX = (int)MathF.Floor(position.X + HalfWidth - eps);
        int minZ = (int)MathF.Floor(position.Z - HalfWidth + eps);
        int maxZ = (int)MathF.Floor(position.Z + HalfWidth - eps);

        for (int tz = minZ; tz <= maxZ; tz++)
        for (int tx = minX; tx <= maxX; tx++)
        {
            var b = world.GetBlockByPositionGlobalSafe(tx, tileY, tz);
            if (b is not null && b.Value.BlockType is not BlockType.None and not BlockType.WaterLevel)
                return true; // ceiling within one block above head
        }
        return false;
    }

    private bool HasBlockAbove(Vector3 globalPosition)
    {
        var chunkIndex = VoxelHelper.GetChunkIndexFromPositionGlobal(globalPosition);
        var chunk = world[chunkIndex];
        if (chunk is null) return false;

        var localPosition = globalPosition - chunk.Position;
        if (localPosition.Y is < 0 or >= VoxelHelper.ChunkYSize) return false;

        var block = chunk.GetBlockAtLocalPosition(localPosition);
        return block.BlockType is not BlockType.None and not BlockType.WaterLevel;
    }
}
