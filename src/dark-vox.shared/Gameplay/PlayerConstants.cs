namespace DarkVox.Shared.Gameplay;

/// <summary>
/// Shared player physics and collision constants.
/// These values must be identical on client and server to ensure prediction/simulation parity.
/// </summary>
public static class PlayerConstants
{
    // === Collider Dimensions ===
    /// <summary>Player cylinder radius (half-width).</summary>
    public const float HalfWidth = 0.3f;
    
    /// <summary>Player total height.</summary>
    public const float Height = 1.7f;
    
    /// <summary>Eye height for camera placement.</summary>
    public const float EyeHeight = 1.6f;
    
    /// <summary>Step height for automatic step-up on small ledges.</summary>
    public const float StepHeight = 0.6f;

    // === Physics Constants ===
    /// <summary>Gravitational acceleration (negative = down).</summary>
    public const float Gravity = -20.0f;
    
    /// <summary>Base movement speed in blocks/second.</summary>
    public const float MoveSpeed = 6.0f;
    
    /// <summary>Vertical velocity applied when jumping.</summary>
    public const float JumpForce = 8.5f;
    
    /// <summary>Ground friction coefficient.</summary>
    public const float Friction = 6.0f;
    
    /// <summary>Air control multiplier (0..1).</summary>
    public const float AirControl = 0.2f;
    
    /// <summary>Maximum physics sub-step duration for stability.</summary>
    public const float MaxPhysicsStepSeconds = 1f / 90f;
    
    /// <summary>
    /// Horizontal velocity damping applied when jumping (0..1).
    /// Lower values prevent overjumping on narrow ledges.
    /// </summary>
    public const float JumpHorizontalDamping = 0.4f;

    // === Speed Modifiers ===
    /// <summary>Sprint speed multiplier.</summary>
    public const float SprintMultiplier = 1.5f;
    
    /// <summary>Crouch speed multiplier.</summary>
    public const float CrouchMultiplier = 0.5f;
    
    /// <summary>Ghost/fly mode speed multiplier.</summary>
    public const float GhostModeMultiplier = 4.0f;

    // === Camera/Look ===
    /// <summary>Mouse look rotation speed multiplier.</summary>
    public const float RotationSpeed = 10.0f;

    // === Combat ===
    /// <summary>Attack cooldown in seconds.</summary>
    public const float AttackCooldown = 0.5f;
    
    /// <summary>Invulnerability duration after taking damage.</summary>
    public const float InvulnerabilityDuration = 0.5f;
    
    /// <summary>Base attack damage.</summary>
    public const int BaseAttackDamage = 1;
    
    /// <summary>Attack reach distance in blocks.</summary>
    public const float AttackReach = 4.0f;
}
