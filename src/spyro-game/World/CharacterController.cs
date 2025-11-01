using OpenTK.Mathematics;

namespace SpyroGame.World;

internal sealed class CharacterController
{
    private readonly VoxelWorld world;

    // Configurable constants (tune as needed)
    public float HalfWidth = 0.35f;              // horizontal half-extent
    public float StandHeight = 1.8f;             // standing collider height
    public float CrawlHeight = 1.0f;             // crawl collider height
    public float EyeHeightStand = 1.6f;          // eye height when standing
    public float EyeHeightCrawl = 0.9f;          // eye height when crawling
    public float Gravity = -9.8f;                // blocks per second^2
    public float MoveSpeed = 2.0f;               // blocks per second
    public float CrawlSpeedMultiplier = 0.55f;   // slower in tunnels
    public float StepHeight = 1.0f;              // max auto step height
    public float StepJumpHeight = 0.6f;          // jump height used for auto-climb
    public float JumpHeight = 1.2f;              // target jump height (>=1 block)

    // State
    public Vector3 Position;
    public float VelocityY;
    public bool IsGrounded;
    public bool IsJumping;
    public bool IsCrawling;
    public float EyeHeight => IsCrawling ? EyeHeightCrawl : EyeHeightStand;

    // Fixed-step accumulator
    private float accumulator;
    private const float FixedDt = 1f / 60f; // 16.6 ms
    private const int MaxStepsPerFrame = 8;

    // Jump request flag (latched for the next simulation step)
    private bool jumpRequested;

    public CharacterController(VoxelWorld world, Vector3 startPosition)
    {
        this.world = world;
        Position = startPosition;
        ResolveInitialPenetration(4);
    }

    public void QueueJump() => jumpRequested = true;

    public void Update(float elapsedSeconds, in Vector3 inputXZ, bool ghostMode)
    {
        var frame = MathF.Min(elapsedSeconds, 0.25f);
        accumulator += frame;

        var steps = 0;
        while (accumulator >= FixedDt && steps < MaxStepsPerFrame)
        {
            Step(FixedDt, inputXZ, ghostMode);
            accumulator -= FixedDt;
            steps++;
        }
    }

    private void Step(float dt, in Vector3 inputXZ, bool ghostMode)
    {
        if (ghostMode)
        {
            var dir = inputXZ;
            if (dir.X != 0 || dir.Z != 0)
            {
                var len = dir.Length;
                if (len > 0) dir /= len;
                Position.X += dir.X * MoveSpeed * 8f * dt;
                Position.Z += dir.Z * MoveSpeed * 8f * dt;
            }
            VelocityY = 0;
            IsGrounded = false;
            IsJumping = false;
            return;
        }

        // Determine stance (auto crawl if limited headroom)
        IsCrawling = HeadBlocked(Position, StandHeight) && !HeadBlocked(Position, CrawlHeight);
        var height = IsCrawling ? CrawlHeight : StandHeight;

        // Jump request
        if (jumpRequested && IsGrounded && !HeadBlocked(Position, height + 0.5f))
        {
            VelocityY = MathF.Sqrt(2f * JumpHeight * -Gravity);
            IsJumping = true;
            IsGrounded = false;
        }
        jumpRequested = false;

        // Compute desired vertical delta first
        var dy = VelocityY * dt + 0.5f * Gravity * dt * dt;
        VelocityY += Gravity * dt;
        var goingUp = dy > 0f;

        // Horizontal speed from input always applies (do not clear on jump)
        var speed = (IsCrawling ? MoveSpeed * CrawlSpeedMultiplier : MoveSpeed);
        var dx = inputXZ.X; var dz = inputXZ.Z;
        var mag = MathF.Sqrt(dx * dx + dz * dz);
        if (mag > 0f) { dx /= mag; dz /= mag; }
        dx *= speed * dt; dz *= speed * dt;

        const float eps = 0.001f;

        if (goingUp && dy != 0)
        {
            // Move up first to avoid face clamping killing forward jump
            var ny = Position.Y + dy;
            if (CollidesAtYTop(Position.X, ny, height, Position.Z, out var clampedY))
            {
                ny = clampedY;
                VelocityY = 0;
            }
            Position.Y = ny;
        }

        // Horizontal motion (axis-sweep X then Z), no auto-step to avoid teleport feeling
        if (dx != 0)
        {
            var nx = Position.X + dx;
            if (CollidesAtX(nx, Position.Y, height, Position.Z, out var clampedX))
            {
                // If moving upward or about to auto-jump, attempt a small raise to preserve forward motion
                var nearOrthX = MathF.Abs(dx) >= MathF.Abs(dz);
                var canAutoJump = IsGrounded && nearOrthX && !HeadBlocked(Position, height + 0.5f);
                if (canAutoJump && VelocityY <= 0)
                {
                    jumpRequested = true;
                    // start a small jump but keep horizontal intent
                    VelocityY = MathF.Sqrt(2f * JumpHeight * -Gravity);
                    IsJumping = true;
                    IsGrounded = false;
                }
            }
            Position.X = nx;
        }

        if (dz != 0)
        {
            var nz = Position.Z + dz;
            if (CollidesAtZ(Position.X, Position.Y, height, nz, out var clampedZ))
            {
                var nearOrthZ = MathF.Abs(dz) >= MathF.Abs(dx);
                var canAutoJump = IsGrounded && nearOrthZ && !HeadBlocked(Position, height + 0.5f);
                if (canAutoJump && VelocityY <= 0)
                {
                    jumpRequested = true;

                    VelocityY = MathF.Sqrt(2f * JumpHeight * -Gravity);
                    IsJumping = true;
                    IsGrounded = false;
                }
            }
            Position.Z = nz;
        }

        if (!goingUp && dy != 0)
        {
            // Move down after horizontal to land cleanly
            var ny = Position.Y + dy;
            if (CollidesAtYBottom(Position.X, ny, height, Position.Z, out var clampedY))
            {
                ny = clampedY;
                VelocityY = 0;
                IsGrounded = true;
                IsJumping = false;
            }
            else
            {
                IsGrounded = false;
            }
            Position.Y = ny;
        }
    }

   

    private bool TryStepUp(float height, float stepMax, float dx, float dz)
    {
        // Incremental step-up to avoid "teleport" feeling; raise only as much as needed this step
        const float stepNudge = 0.2f;
        var oldY = Position.Y;
        for (var inc = stepNudge; inc <= stepMax + 1e-5f; inc += stepNudge)
        {
            var testY = oldY + inc;
            if (AabbOccupied(Position.X, testY, height, Position.Z))
                continue; // body would intersect if raised here; try higher

            bool success;
            if (dx != 0)
                success = !CollidesAtX(Position.X + dx, testY, height, Position.Z, out _);
            else
                success = !CollidesAtZ(Position.X, testY, height, Position.Z + dz, out _);

            if (success)
            {
                Position.Y = testY; // raise only minimally this step
                return true;
            }
        }
        return false;
    }

    // Collision spans
    private bool CollidesAtX(float nx, float y, float h, float z, out float clampedX)
    {
        const float eps = 0.001f;
        var minY = (int)MathF.Floor(y);
        var maxY = (int)MathF.Floor(y + h - eps);
        var minZ = (int)MathF.Floor(z - HalfWidth + eps);
        var maxZ = (int)MathF.Floor(z + HalfWidth - eps);

        if (nx > Position.X)
        {
            var tileX = (int)MathF.Floor(nx + HalfWidth);
            if (AnySolidAtX(tileX, minY, maxY, minZ, maxZ))
            {
                clampedX = tileX - HalfWidth - eps;
                return true;
            }
        }
        else
        {
            var tileX = (int)MathF.Floor(nx - HalfWidth);
            if (AnySolidAtX(tileX, minY, maxY, minZ, maxZ))
            {
                clampedX = tileX + 1 + HalfWidth + eps;
                return true;
            }
        }
        clampedX = nx;
        return false;
    }

    private bool CollidesAtZ(float x, float y, float h, float nz, out float clampedZ)
    {
        const float eps = 0.001f;
        var minY = (int)MathF.Floor(y);
        var maxY = (int)MathF.Floor(y + h - eps);
        var minX = (int)MathF.Floor(x - HalfWidth + eps);
        var maxX = (int)MathF.Floor(x + HalfWidth - eps);

        if (nz > Position.Z)
        {
            var tileZ = (int)MathF.Floor(nz + HalfWidth);
            if (AnySolidAtZ(tileZ, minY, maxY, minX, maxX))
            {
                clampedZ = tileZ - HalfWidth - eps;
                return true;
            }
        }
        else
        {
            var tileZ = (int)MathF.Floor(nz - HalfWidth);
            if (AnySolidAtZ(tileZ, minY, maxY, minX, maxX))
            {
                clampedZ = tileZ + 1 + HalfWidth + eps;
                return true;
            }
        }
        clampedZ = nz;
        return false;
    }

    private bool CollidesAtYTop(float x, float ny, float h, float z, out float clampedY)
    {
        const float eps = 0.001f;
        var tileY = (int)MathF.Floor(ny + h);
        var minX = (int)MathF.Floor(x - HalfWidth + eps);
        var maxX = (int)MathF.Floor(x + HalfWidth - eps);
        var minZ = (int)MathF.Floor(z - HalfWidth + eps);
        var maxZ = (int)MathF.Floor(z + HalfWidth - eps);

        if (AnySolidAtY(tileY, minX, maxX, minZ, maxZ))
        {
            clampedY = tileY - h - eps;
            return true;
        }
        clampedY = ny;
        return false;
    }

    private bool CollidesAtYBottom(float x, float ny, float h, float z, out float clampedY)
    {
        const float eps = 0.001f;
        var tileY = (int)MathF.Floor(ny);
        var minX = (int)MathF.Floor(x - HalfWidth + eps);
        var maxX = (int)MathF.Floor(x + HalfWidth - eps);
        var minZ = (int)MathF.Floor(z - HalfWidth + eps);
        var maxZ = (int)MathF.Floor(z + HalfWidth - eps);

        if (AnySolidAtY(tileY, minX, maxX, minZ, maxZ))
        {
            clampedY = tileY + 1 + eps;
            return true;
        }
        clampedY = ny;
        return false;
    }

    private bool AnySolidAtX(int tileX, int minY, int maxY, int minZ, int maxZ)
    {
        for (var y = minY; y <= maxY; y++)
            for (var z = minZ; z <= maxZ; z++)
                if (IsSolid(tileX, y, z)) return true;
        return false;
    }
    private bool AnySolidAtZ(int tileZ, int minY, int maxY, int minX, int maxX)
    {
        for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
                if (IsSolid(x, y, tileZ)) return true;
        return false;
    }
    private bool AnySolidAtY(int tileY, int minX, int maxX, int minZ, int maxZ)
    {
        for (var z = minZ; z <= maxZ; z++)
            for (var x = minX; x <= maxX; x++)
                if (IsSolid(x, tileY, z)) return true;
        return false;
    }

    private bool IsSolid(int x, int y, int z)
    {
        var b = world.GetBlockByPositionGlobalSafe(x, y, z);
        return b is not null && b.Value.BlockType is not BlockType.None and not BlockType.WaterLevel;
    }

    private bool HeadBlocked(in Vector3 pos, float height)
    {
        var px = (int)MathF.Floor(pos.X);
        var py = (int)MathF.Floor(pos.Y + height - 0.05f);
        var pz = (int)MathF.Floor(pos.Z);
        return IsSolid(px, py, pz);
    }

    private void ResolveInitialPenetration(int maxUp)
    {
        // If starting inside terrain, nudge up to find free space
        var h = StandHeight;
        for (var i = 0; i < maxUp; i++)
        {
            if (!AabbOccupied(Position.X, Position.Y, h, Position.Z)) return;
            Position.Y += 1.0f;
        }
    }

    private bool AabbOccupied(float x, float y, float h, float z)
    {
        const float eps = 0.001f;
        var minX = (int)MathF.Floor(x - HalfWidth + eps);
        var maxX = (int)MathF.Floor(x + HalfWidth - eps);
        var minZ = (int)MathF.Floor(z - HalfWidth + eps);
        var maxZ = (int)MathF.Floor(z + HalfWidth - eps);
        var minY = (int)MathF.Floor(y);
        var maxY = (int)MathF.Floor(y + h - eps);
        for (var ty = minY; ty <= maxY; ty++)
            for (var tz = minZ; tz <= maxZ; tz++)
                for (var tx = minX; tx <= maxX; tx++)
                    if (IsSolid(tx, ty, tz)) return true;
        return false;
    }
}
