using OpenTK.Mathematics;
using DarkVox.Shared.Abstractions;
using DarkVox.Shared.Gameplay;
using DarkVox.Shared.Input;
using DarkVox.Shared.State;
using DarkVox.Shared.World;
using DarkVox.Shared.World.Registry;
using DarkVox.World;

namespace DarkVox.Server.Gameplay;

/// <summary>
/// Server-authoritative player simulation.
/// This deliberately contains only gameplay and physics state (no rendering/camera).
/// Uses shared constants and physics from <see cref="PlayerConstants"/> and <see cref="PlayerPhysicsCore"/>.
/// </summary>
public sealed class Player : ILootCollector
{
    // Use shared collider from PlayerPhysicsCore
    public static KinematicCollider Collider => PlayerPhysicsCore.CreateCollider();

    private static readonly Vector3[] bottomCornerOffsets =
    [
        new Vector3(-PlayerConstants.HalfWidth, 0, -PlayerConstants.HalfWidth),
        new Vector3(-PlayerConstants.HalfWidth, 0, +PlayerConstants.HalfWidth),
        new Vector3(+PlayerConstants.HalfWidth, 0, -PlayerConstants.HalfWidth),
        new Vector3(+PlayerConstants.HalfWidth, 0, +PlayerConstants.HalfWidth),
    ];

    private readonly VoxelWorld world;
    private IBlockEditService? blockEdits;

    private Vector3 position;
    private bool isGrounded;
    private Vector3 velocity;

    private Vector2 moveAxes;
    private float verticalAxis;
    private bool jumpRequested;

    private bool isSprinting;
    private bool isCrouching;

    private float attackCooldownRemaining;
    private float invulnerabilityRemaining;

    // Exhaustion tracking for current tick
    private bool jumpedThisTick;
    private bool attackedThisTick;
    private int blocksBrokenThisTick;

    public PlayerAttributes Attributes { get; } = new();
    public Inventory Inventory { get; } = new();

    public Player(VoxelWorld world, Vector3 spawnPosition, IBlockEditService? blockEdits = null)
    {
        this.world = world;
        this.blockEdits = blockEdits;

        Position = spawnPosition;
        Direction = -Vector3.UnitZ;

        isGrounded = false;
        velocity = Vector3.Zero;
    }

    public IBlockEditService? BlockEdits
    {
        get => blockEdits;
        set => blockEdits = value;
    }

    public bool IsAlive => Attributes.IsAlive;

    public Vector3 Position
    {
        get => position;
        set => position = value;
    }

    public Vector3 Direction { get; set; }

    internal bool IsGrounded => isGrounded;
    internal float VelocityY => velocity.Y;

    public Vector3 Velocity => velocity;

    public float InvulnerabilityRemaining => invulnerabilityRemaining;

    public Vector3 RequestedMovement { get; private set; }

    private bool isGhostMode;
    public bool IsGhostMode
    {
        get => isGhostMode;
        set
        {
            if (isGhostMode == value) return;
            isGhostMode = value;
            velocity = Vector3.Zero;
            isGrounded = false;
        }
    }

    public void AddItem(GameObjectId item, int count) => Inventory.AddItem(item, count);

    public void ApplyInput(PlayerInputCommand input)
    {
        if (input.SetGhostMode is { } ghost)
        {
            IsGhostMode = ghost;
        }

        isSprinting = input.SprintHeld;
        isCrouching = input.CrouchHeld;

        moveAxes = input.MoveAxes;
        verticalAxis = input.VerticalAxis;

        // Apply look rotation using shared physics core
        if (input.LookDelta != Vector2.Zero)
        {
            Direction = PlayerPhysicsCore.ApplyLookRotation(Direction, input.LookDelta.X, input.LookDelta.Y);
        }

        if (input.JumpPressed)
        {
            jumpRequested = true;
        }

        if (input.SelectHotbarSlot.HasValue)
        {
            Inventory.SelectedSlot = input.SelectHotbarSlot.Value;
        }

        if (input.HotbarScrollDelta != 0)
        {
            Inventory.SelectedSlot += input.HotbarScrollDelta;
            if (Inventory.SelectedSlot < 0) Inventory.SelectedSlot = Inventory.HotbarSize - 1;
            if (Inventory.SelectedSlot >= Inventory.HotbarSize) Inventory.SelectedSlot = 0;
        }
    }

    public void Simulate(double elapsedSeconds)
    {
        // Use shared physics for movement vector calculation
        RequestedMovement = PlayerPhysicsCore.BuildMovementVector(Direction, moveAxes, verticalAxis, IsGhostMode);
        var isMoving = RequestedMovement.LengthSquared > 0.0001f;

        // Track if we jumped this tick
        var didJump = false;
        if (jumpRequested)
        {
            jumpRequested = false;
            didJump = Jump();
        }

        if (IsGhostMode)
        {
            HandleGhostMode(elapsedSeconds);
        }
        else
        {
            HandleMovement(elapsedSeconds);
        }

        // Tick attributes with exhaustion context
        Attributes.Tick(elapsedSeconds, new PlayerAttributeTickContext(
            IsMoving: isMoving, 
            IsSprinting: isSprinting, 
            IsGhostMode: IsGhostMode,
            JumpedThisTick: didJump || jumpedThisTick,
            AttackedThisTick: attackedThisTick,
            BlocksBrokenThisTick: blocksBrokenThisTick));

        // Reset per-tick exhaustion tracking
        jumpedThisTick = false;
        attackedThisTick = false;
        blocksBrokenThisTick = 0;

        if (attackCooldownRemaining > 0)
            attackCooldownRemaining -= (float)elapsedSeconds;
        if (invulnerabilityRemaining > 0)
            invulnerabilityRemaining -= (float)elapsedSeconds;

        RequestedMovement = Vector3.Zero;
    }

    /// <summary>
    /// Attempts to jump. Returns true if the jump was successful.
    /// </summary>
    public bool Jump()
    {
        if (isGrounded && !IsUpBlocked() && !IsInLowHeadroomTunnel())
        {
            isGrounded = false;
            velocity.Y = PlayerConstants.JumpForce;
            velocity.X *= PlayerConstants.JumpHorizontalDamping;
            velocity.Z *= PlayerConstants.JumpHorizontalDamping;
            return true;
        }
        return false;
    }

    public void TryBreakBlock(Vector3i globalPosition)
    {
        if (blockEdits == null) return;

        var existing = world.GetBlockByPositionGlobalSafe(globalPosition.X, globalPosition.Y, globalPosition.Z);
        if (!existing.HasValue) return;
        if (existing.Value.Block.IsAir()) return;

        var blockId = existing.Value.Block;
        var blockDef = GameContentRegistry.GetBlock(blockId);
        var drops = blockDef.LootTable.GenerateDrops(blockId);

        foreach ((GameObjectId item, int count) in drops)
        {
            Inventory.AddItem(item, count);
        }

        blockEdits.ApplyBlockEdit(globalPosition, BlockId.Air, true);
        
        // Track block breaking for exhaustion
        blocksBrokenThisTick++;
    }

    public void TryPlaceBlock(Vector3i placePos, BlockId blockId)
    {
        if (blockEdits == null) return;
        if (blockId.IsAir()) return;

        var targetBlock = world.GetBlockByPositionGlobalSafe(placePos.X, placePos.Y, placePos.Z);
        if (targetBlock.HasValue && !targetBlock.Value.Block.IsReplaceable())
        {
            return;
        }

        if (!HasSolidCardinalNeighbor(placePos))
        {
            return;
        }

        // Check player-block intersection using shared constants
        var minX = position.X - PlayerConstants.HalfWidth;
        var maxX = position.X + PlayerConstants.HalfWidth;
        var minY = position.Y;
        var maxY = position.Y + PlayerConstants.Height;

        var bMinX = placePos.X;
        var bMaxX = placePos.X + 1;
        var bMinY = placePos.Y;
        var bMaxY = placePos.Y + 1;
        var bMinZ = placePos.Z;
        var bMaxZ = placePos.Z + 1;

        var intersects = (minX < bMaxX && maxX > bMinX) &&
                         (minY < bMaxY && maxY > bMinY) &&
                         (position.Z - PlayerConstants.HalfWidth < bMaxZ && position.Z + PlayerConstants.HalfWidth > bMinZ);

        if (intersects && blockId.IsSolid())
        {
            return;
        }

        blockEdits.ApplyBlockEdit(placePos, blockId, isBreaking: false);
        Inventory.TryConsumeSelectedItem();
    }

    public bool TryEatFood()
    {
        var selectedItem = Inventory.SelectedItem;
        if (selectedItem.IsEmpty) return false;

        var foodItem = GameContentRegistry.GetFood(selectedItem.Item);
        if (foodItem == null) return false;

        var consumed = Attributes.ConsumeFood(
            foodItem.Nutrition,
            foodItem.SaturationRestored,
            foodItem.CanAlwaysEat);

        if (!consumed) return false;

        Inventory.TryConsumeSelectedItem();

        if (Attributes.Health < Attributes.MaxHealth)
        {
            Attributes.Heal(1);
        }

        return true;
    }

    public bool CanAttack() => attackCooldownRemaining <= 0;

    public void StartAttackCooldown(float attackSpeed)
    {
        attackCooldownRemaining = attackSpeed > 0 ? 1f / attackSpeed : PlayerConstants.AttackCooldown;
        
        // Track attack for exhaustion
        attackedThisTick = true;
    }

    public void StartInvulnerability(float seconds)
    {
        invulnerabilityRemaining = seconds;
    }

    public void ApplyKnockback(Vector3 horizontalKnockback, float verticalKnockback)
    {
        velocity.X += horizontalKnockback.X;
        velocity.Z += horizontalKnockback.Z;
        if (isGrounded || velocity.Y < verticalKnockback)
        {
            velocity.Y = verticalKnockback;
        }
    }

    public PlayerSnapshot BuildSnapshot(int selectedHotbarSlot)
    {
        return new PlayerSnapshot(
            Position: position,
            Direction: Direction,
            Velocity: velocity,
            IsGrounded: isGrounded,
            IsGhostMode: IsGhostMode,
            SelectedHotbarSlot: selectedHotbarSlot,
            Inventory: Inventory.BuildSnapshot(),
            Attributes: Attributes.BuildSnapshot());
    }

    /// <summary>
    /// Builds save data for persistence.
    /// </summary>
    public PlayerSaveData BuildSaveData()
    {
        return PlayerSaveData.FromSnapshot(
            position,
            Direction,
            IsGhostMode,
            Attributes.BuildSnapshot(),
            Inventory.BuildSnapshot(),
            Inventory.SelectedSlot);
    }

    /// <summary>
    /// Applies loaded save data to restore player state.
    /// </summary>
    public void ApplySaveData(PlayerSaveData saveData)
    {
        // Position and direction
        position = saveData.GetPosition();
        Direction = saveData.GetDirection();
        IsGhostMode = saveData.IsGhostMode;

        // Attributes
        Attributes.ApplySnapshot(new PlayerAttributesSnapshot(
            MaxHealth: saveData.MaxHealth,
            Health: saveData.Health,
            MaxFood: saveData.MaxFood,
            Food: saveData.Food,
            Saturation: saveData.Saturation,
            Exhaustion: saveData.Exhaustion));

        // Inventory
        Inventory.SelectedSlot = saveData.SelectedHotbarSlot;
        foreach (var slot in saveData.InventorySlots)
        {
            if (!string.IsNullOrEmpty(slot.ItemId) && slot.Count > 0)
            {
                if (GameObjectIdExtensions.TryParse(slot.ItemId, out var itemId))
                {
                    Inventory.SetItem(slot.SlotIndex, new InventoryItem { Item = itemId, Count = slot.Count });
                }
            }
        }
    }

    private void HandleGhostMode(double elapsedSeconds)
    {
        velocity = Vector3.Zero;
        var moveInput = RequestedMovement;
        var remaining = (float)elapsedSeconds;

        while (remaining > 0f && moveInput.LengthSquared > 0f)
        {
            var dt = MathF.Min(remaining, PlayerConstants.MaxPhysicsStepSeconds);
            var dir = moveInput.Normalized();
            position += dir * dt * PlayerConstants.MoveSpeed * PlayerConstants.GhostModeMultiplier;
            remaining -= dt;
        }
    }

    private void HandleMovement(double elapsedSeconds)
    {
        var moveInput = RequestedMovement;
        var remaining = (float)elapsedSeconds;
        var steps = 0;

        while (remaining > 0f)
        {
            var dt = MathF.Min(remaining, PlayerConstants.MaxPhysicsStepSeconds);
            ApplyMovementStep(moveInput, dt);
            remaining -= dt;
            steps++;

            if (steps > 64)
            {
                break;
            }
        }
    }

    private void ApplyMovementStep(Vector3 moveInput, float dt)
    {
        velocity.Y += PlayerConstants.Gravity * dt;

        var wishDir = Vector3.Zero;
        if (moveInput.LengthSquared > 0.001f)
        {
            wishDir = moveInput.Normalized();
        }

        var speed = PlayerConstants.MoveSpeed;
        if (isSprinting) speed *= PlayerConstants.SprintMultiplier;
        if (isCrouching) speed *= PlayerConstants.CrouchMultiplier;

        if (isGrounded)
        {
            velocity.X = wishDir.X * speed;
            velocity.Z = wishDir.Z * speed;
        }
        else if (wishDir.LengthSquared > 0.001f)
        {
            Accelerate(wishDir, speed, PlayerConstants.AirControl * 10.0f, dt);
        }

        Move(velocity * dt);

        var maxSpeed = isSprinting 
            ? PlayerConstants.MoveSpeed * PlayerConstants.SprintMultiplier 
            : (isCrouching 
                ? PlayerConstants.MoveSpeed * PlayerConstants.CrouchMultiplier 
                : PlayerConstants.MoveSpeed);
        var hVel = new Vector2(velocity.X, velocity.Z);
        if (hVel.LengthSquared > maxSpeed * maxSpeed)
        {
            hVel = hVel.Normalized() * maxSpeed;
            velocity.X = hVel.X;
            velocity.Z = hVel.Y;
        }
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
        VoxelKinematicMover.Move(
            world,
            ref position,
            ref velocity,
            ref isGrounded,
            Collider,
            delta,
            worldFloorY: 1.0f,
            enableAutoJump: true,
            gravityMagnitude: MathF.Abs(PlayerConstants.Gravity),
            autoJumpHorizontalDamping: PlayerConstants.JumpHorizontalDamping);
    }

    private bool HasSolidCardinalNeighbor(Vector3i pos)
    {
        Vector3i[] neighbors =
        [
            new(pos.X - 1, pos.Y, pos.Z),
            new(pos.X + 1, pos.Y, pos.Z),
            new(pos.X, pos.Y - 1, pos.Z),
            new(pos.X, pos.Y + 1, pos.Z),
            new(pos.X, pos.Y, pos.Z - 1),
            new(pos.X, pos.Y, pos.Z + 1)
        ];

        foreach (var neighbor in neighbors)
        {
            var block = world.GetBlockByPositionGlobalSafe(neighbor.X, neighbor.Y, neighbor.Z);
            if (block.HasValue && block.Value.IsSolid)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsUpBlocked()
    {
        if (IsGhostMode) return false;

        var c1 = position - bottomCornerOffsets[0];
        var c2 = position - bottomCornerOffsets[1];
        var c3 = position - bottomCornerOffsets[2];
        var c4 = position - bottomCornerOffsets[3];
        c1.Y += PlayerConstants.Height + 0.3f;
        c2.Y += PlayerConstants.Height + 0.3f;
        c3.Y += PlayerConstants.Height + 0.3f;
        c4.Y += PlayerConstants.Height + 0.3f;
        return HasBlockAbove(c1) || HasBlockAbove(c2) || HasBlockAbove(c3) || HasBlockAbove(c4);
    }

    private bool HasBlockAbove(Vector3 globalPosition)
    {
        var x = (int)globalPosition.X;
        var y = (int)globalPosition.Y;
        var z = (int)globalPosition.Z;

        var block = world.GetBlockByPositionGlobalSafe(x, y, z);
        return block is not null && block.Value.IsSolid;
    }

    private bool IsInLowHeadroomTunnel()
    {
        if (IsGhostMode) return false;
        const float eps = 0.001f;

        var headTop = position.Y + PlayerConstants.Height - 0.05f;
        var tileY = (int)MathF.Floor(headTop) + 1;
        var minX = (int)MathF.Floor(position.X - PlayerConstants.HalfWidth + eps);
        var maxX = (int)MathF.Floor(position.X + PlayerConstants.HalfWidth - eps);
        var minZ = (int)MathF.Floor(position.Z - PlayerConstants.HalfWidth + eps);
        var maxZ = (int)MathF.Floor(position.Z + PlayerConstants.HalfWidth - eps);

        for (var tz = minZ; tz <= maxZ; tz++)
            for (var tx = minX; tx <= maxX; tx++)
            {
                var b = world.GetBlockByPositionGlobalSafe(tx, tileY, tz);
                if (b is not null && b.Value.IsSolid)
                    return true;
            }

        return false;
    }
}
