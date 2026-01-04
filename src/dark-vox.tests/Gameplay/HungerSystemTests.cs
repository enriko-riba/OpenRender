using DarkVox.Shared.Gameplay;
using Xunit;

namespace DarkVox.Tests.Gameplay;

/// <summary>
/// Tests for Minecraft-style hunger mechanics.
/// </summary>
public class HungerSystemTests
{
    [Fact]
    public void Walking_ShouldDrainExhaustion()
    {
        var attrs = new PlayerAttributes();
        var initialSaturation = 5.0f;
        
        // Walk for a long time to accumulate exhaustion
        // Walking rate: 0.043 exhaustion/sec
        // After ~93 seconds: 4.0 exhaustion = 1 saturation drained
        var ctx = new PlayerAttributeTickContext(IsMoving: true, IsSprinting: false, IsGhostMode: false);
        
        // Simulate 100 seconds of walking
        for (int i = 0; i < 100; i++)
        {
            attrs.Tick(1.0, ctx);
        }
        
        // Should have drained at least 1 saturation
        Assert.True(attrs.Saturation < initialSaturation, 
            $"Expected saturation to drain from walking. Initial: {initialSaturation}, Current: {attrs.Saturation}");
    }
    
    [Fact]
    public void Sprinting_ShouldDrainFasterThanWalking()
    {
        var walkingAttrs = new PlayerAttributes();
        var sprintingAttrs = new PlayerAttributes();
        
        var walkCtx = new PlayerAttributeTickContext(IsMoving: true, IsSprinting: false, IsGhostMode: false);
        var sprintCtx = new PlayerAttributeTickContext(IsMoving: true, IsSprinting: true, IsGhostMode: false);
        
        // Simulate 30 seconds
        for (int i = 0; i < 30; i++)
        {
            walkingAttrs.Tick(1.0, walkCtx);
            sprintingAttrs.Tick(1.0, sprintCtx);
        }
        
        // Sprinting should have drained more
        Assert.True(sprintingAttrs.Saturation < walkingAttrs.Saturation,
            $"Sprinting should drain faster. Walk: {walkingAttrs.Saturation}, Sprint: {sprintingAttrs.Saturation}");
    }
    
    [Fact]
    public void GhostMode_ShouldNotDrainHunger()
    {
        var attrs = new PlayerAttributes();
        var initialFood = attrs.Food;
        var initialSaturation = attrs.Saturation;
        
        var ctx = new PlayerAttributeTickContext(IsMoving: true, IsSprinting: true, IsGhostMode: true);
        
        // Simulate 1000 seconds in ghost mode
        for (int i = 0; i < 1000; i++)
        {
            attrs.Tick(1.0, ctx);
        }
        
        Assert.Equal(initialFood, attrs.Food);
        Assert.Equal(initialSaturation, attrs.Saturation);
    }
    
    [Fact]
    public void Jumping_ShouldAddExhaustion()
    {
        var attrs = new PlayerAttributes();
        var initialSaturation = attrs.Saturation;
        
        // Jump many times to accumulate exhaustion
        // Jump exhaustion: 0.05 per jump, 0.2 when sprinting
        // Need 80 jumps for 4.0 exhaustion (1 saturation)
        for (int i = 0; i < 100; i++)
        {
            var ctx = new PlayerAttributeTickContext(
                IsMoving: false, 
                IsSprinting: false, 
                IsGhostMode: false,
                JumpedThisTick: true);
            attrs.Tick(0.1, ctx);
        }
        
        Assert.True(attrs.Saturation < initialSaturation,
            $"Jumping should drain saturation. Initial: {initialSaturation}, Current: {attrs.Saturation}");
    }
    
    [Fact]
    public void SprintJumping_ShouldDrainFasterThanNormalJumping()
    {
        var normalJumpAttrs = new PlayerAttributes();
        var sprintJumpAttrs = new PlayerAttributes();
        
        // 20 jumps
        for (int i = 0; i < 20; i++)
        {
            var normalCtx = new PlayerAttributeTickContext(
                IsMoving: false, IsSprinting: false, IsGhostMode: false, JumpedThisTick: true);
            var sprintCtx = new PlayerAttributeTickContext(
                IsMoving: true, IsSprinting: true, IsGhostMode: false, JumpedThisTick: true);
            
            normalJumpAttrs.Tick(0.1, normalCtx);
            sprintJumpAttrs.Tick(0.1, sprintCtx);
        }
        
        Assert.True(sprintJumpAttrs.Saturation < normalJumpAttrs.Saturation,
            $"Sprint-jumping should drain faster. Normal: {normalJumpAttrs.Saturation}, Sprint: {sprintJumpAttrs.Saturation}");
    }
    
    [Fact]
    public void Attacking_ShouldAddExhaustion()
    {
        var attrs = new PlayerAttributes();
        var initialSaturation = attrs.Saturation;
        
        // Attack exhaustion: 0.1 per attack
        // Need 40 attacks for 4.0 exhaustion (1 saturation)
        for (int i = 0; i < 50; i++)
        {
            var ctx = new PlayerAttributeTickContext(
                IsMoving: false, 
                IsSprinting: false, 
                IsGhostMode: false,
                AttackedThisTick: true);
            attrs.Tick(0.1, ctx);
        }
        
        Assert.True(attrs.Saturation < initialSaturation,
            $"Attacking should drain saturation. Initial: {initialSaturation}, Current: {attrs.Saturation}");
    }
    
    [Fact]
    public void BreakingBlocks_ShouldAddExhaustion()
    {
        var attrs = new PlayerAttributes();
        var initialSaturation = attrs.Saturation;
        
        // Block breaking exhaustion: 0.005 per block
        // Need 800 blocks for 4.0 exhaustion (1 saturation)
        for (int i = 0; i < 1000; i++)
        {
            var ctx = new PlayerAttributeTickContext(
                IsMoving: false, 
                IsSprinting: false, 
                IsGhostMode: false,
                BlocksBrokenThisTick: 1);
            attrs.Tick(0.1, ctx);
        }
        
        Assert.True(attrs.Saturation < initialSaturation,
            $"Breaking blocks should drain saturation. Initial: {initialSaturation}, Current: {attrs.Saturation}");
    }
    
    [Fact]
    public void SaturationDrainsBeforeFood()
    {
        var attrs = new PlayerAttributes();
        
        // Start with full food (20) and saturation (5)
        Assert.Equal(20, attrs.Food);
        Assert.Equal(5.0f, attrs.Saturation);
        
        // Add exhaustion to drain saturation first
        // 4.0 exhaustion = 1 saturation drained
        attrs.AddExhaustion(4.0f);
        
        Assert.Equal(4.0f, attrs.Saturation); // Saturation should drop by 1
        Assert.Equal(20, attrs.Food); // Food should stay the same
        
        // Drain all saturation
        for (int i = 0; i < 4; i++)
        {
            attrs.AddExhaustion(4.0f);
        }
        
        Assert.Equal(0.0f, attrs.Saturation); // All saturation gone
        Assert.Equal(20, attrs.Food); // Food still full
        
        // Now food should start draining
        attrs.AddExhaustion(4.0f);
        Assert.Equal(19, attrs.Food); // Food should drop by 1
    }
    
    [Fact]
    public void Starvation_ShouldDamagePlayer()
    {
        var attrs = new PlayerAttributes();
        attrs.SetFood(0, 0); // Empty food and saturation
        
        var initialHealth = attrs.Health;
        var ctx = new PlayerAttributeTickContext(IsMoving: false, IsSprinting: false, IsGhostMode: false);
        
        // Starvation deals 1 damage every 4 seconds
        attrs.Tick(4.0, ctx);
        
        Assert.Equal(initialHealth - 1, attrs.Health);
    }
    
    [Fact]
    public void WellFed_ShouldRegenerate()
    {
        var attrs = new PlayerAttributes();
        attrs.Damage(5); // Take some damage
        
        var initialHealth = attrs.Health;
        Assert.Equal(15, initialHealth);
        
        // Food >= 18 triggers regeneration
        var ctx = new PlayerAttributeTickContext(IsMoving: false, IsSprinting: false, IsGhostMode: false);
        
        // Regeneration: 1 HP every 4 seconds when food >= 18
        attrs.Tick(4.0, ctx);
        
        Assert.Equal(16, attrs.Health); // Should have regenerated 1 HP
    }
    
    [Fact]
    public void Regeneration_ShouldCauseExhaustion()
    {
        var attrs = new PlayerAttributes();
        attrs.Damage(5); // Take some damage
        
        var initialSaturation = attrs.Saturation;
        var ctx = new PlayerAttributeTickContext(IsMoving: false, IsSprinting: false, IsGhostMode: false);
        
        // Regeneration causes 6.0 exhaustion per half-heart healed
        attrs.Tick(4.0, ctx);
        
        // 6.0 exhaustion = 1 saturation + 2.0 remaining exhaustion
        Assert.True(attrs.Saturation < initialSaturation,
            $"Regeneration should cause exhaustion. Initial: {initialSaturation}, Current: {attrs.Saturation}");
    }
    
    [Fact]
    public void LowFood_ShouldNotRegenerate()
    {
        var attrs = new PlayerAttributes();
        attrs.Damage(5);
        attrs.SetFood(17, 0); // Just below the regeneration threshold
        
        var initialHealth = attrs.Health;
        var ctx = new PlayerAttributeTickContext(IsMoving: false, IsSprinting: false, IsGhostMode: false);
        
        // Should not regenerate when food < 18
        attrs.Tick(4.0, ctx);
        
        Assert.Equal(initialHealth, attrs.Health);
    }
    
    [Fact]
    public void Snapshot_ShouldPreserveExhaustion()
    {
        var attrs = new PlayerAttributes();
        
        // Add some exhaustion
        attrs.AddExhaustion(2.5f);
        Assert.Equal(2.5f, attrs.Exhaustion, 0.01f);
        
        // Build snapshot
        var snapshot = attrs.BuildSnapshot();
        Assert.Equal(2.5f, snapshot.Exhaustion, 0.01f);
        
        // Apply to new attributes
        var newAttrs = new PlayerAttributes();
        newAttrs.ApplySnapshot(snapshot);
        
        Assert.Equal(2.5f, newAttrs.Exhaustion, 0.01f);
    }
    
    [Fact]
    public void Snapshot_ShouldPreserveFoodAndSaturation()
    {
        var attrs = new PlayerAttributes();
        attrs.SetFood(15, 3.0f);
        
        var snapshot = attrs.BuildSnapshot();
        
        var newAttrs = new PlayerAttributes();
        newAttrs.ApplySnapshot(snapshot);
        
        Assert.Equal(15, newAttrs.Food);
        Assert.Equal(3.0f, newAttrs.Saturation, 0.01f);
    }
}
