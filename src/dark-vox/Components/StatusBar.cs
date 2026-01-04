using OpenRender.Components;
using OpenRender.Core;
using OpenRender.Core.Rendering;
using OpenRender.SceneManagement;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using DarkVox.Shared.Gameplay;

namespace DarkVox.Components;

/// <summary>
/// Minecraft-style status bar displaying health hearts and hunger drumsticks.
/// Positioned above the hotbar.
/// 
/// Required textures:
/// - Resources/gui/heart.png (full heart)
/// - Resources/gui/heart_empty.png (empty heart background)
/// - Resources/gui/heart_half.png (half heart overlay)
/// - Resources/gui/drumstick.png (full drumstick)
/// - Resources/gui/drumstick_empty.png (empty drumstick background)
/// - Resources/gui/drumstick_half.png (half drumstick overlay)
/// </summary>
internal class StatusBar : SceneNode
{
    private const int IconSize = 9;
    private const int ScaleFactor = 3;
    private const int ScaledIconSize = IconSize * ScaleFactor;
    private const int IconSpacing = 8 * ScaleFactor; // Slight overlap like Minecraft
    private const int MaxHearts = 10;
    private const int MaxDrumsticks = 10;
    private const int BarSpacing = 4 * ScaleFactor; // Space between hearts and drumsticks rows

    private readonly PlayerAttributes attributes;
    
    // Heart sprites: base shows empty/full, overlay shows half
    private readonly Sprite[] heartSprites = new Sprite[MaxHearts];
    private readonly Sprite[] heartHalfOverlays = new Sprite[MaxHearts];
    
    // Drumstick sprites: base shows empty/full, overlay shows half
    private readonly Sprite[] drumstickSprites = new Sprite[MaxDrumsticks];
    private readonly Sprite[] drumstickHalfOverlays = new Sprite[MaxDrumsticks];

    // Materials for different states
    private readonly Material heartFullMaterial;
    private readonly Material heartEmptyMaterial;
    private readonly Material heartHalfMaterial;
    private readonly Material drumstickFullMaterial;
    private readonly Material drumstickEmptyMaterial;
    private readonly Material drumstickHalfMaterial;

    private int lastHealth = -1;
    private int lastFood = -1;

    public static StatusBar Create(PlayerAttributes attributes, int screenWidth, int screenHeight)
    {
        // Create a dummy mesh/material for the container node
        var (mesh, material) = Sprite.CreateMeshAndMaterial("Resources/gui/heart.png");
        return new StatusBar(mesh, material, attributes, screenWidth, screenHeight);
    }

    private StatusBar(Mesh mesh, Material material, PlayerAttributes attributes, int screenWidth, int screenHeight)
        : base(mesh, material)
    {
        this.attributes = attributes;
        base.IsVisible = false; // Container itself doesn't render
        RenderGroup = RenderGroup.UI;
        DisableCulling = true;

        // Create materials for heart states
        heartFullMaterial = CreateMaterial("Resources/gui/heart.png");
        heartEmptyMaterial = CreateMaterial("Resources/gui/heart_empty.png");
        heartHalfMaterial = CreateMaterial("Resources/gui/heart_half.png");
        
        // Create materials for drumstick states
        drumstickFullMaterial = CreateMaterial("Resources/gui/drumstick.png");
        drumstickEmptyMaterial = CreateMaterial("Resources/gui/drumstick_empty.png");
        drumstickHalfMaterial = CreateMaterial("Resources/gui/drumstick_half.png");

        // Calculate positions - centered above hotbar
        // Hearts on left, drumsticks on right (like Minecraft)
        var hotbarWidth = HotBar.Width;
        var hotbarY = screenHeight - HotBar.Height - 5;
        var statusY = hotbarY - ScaledIconSize - BarSpacing;

        var centerX = screenWidth / 2f;
        var heartsStartX = centerX - hotbarWidth / 2f;
        var drumsticksStartX = centerX + hotbarWidth / 2f - (MaxDrumsticks * IconSpacing);

        // Create heart sprites (left side)
        for (var i = 0; i < MaxHearts; i++)
        {
            var x = heartsStartX + i * IconSpacing;
            
            // Base heart sprite (shows empty or full)
            heartSprites[i] = CreateIconSprite(heartEmptyMaterial);
            heartSprites[i].SetPosition(new Vector2(x, statusY));
            AddChild(heartSprites[i]);

            // Half heart overlay
            heartHalfOverlays[i] = CreateIconSprite(heartHalfMaterial);
            heartHalfOverlays[i].SetPosition(new Vector2(x, statusY));
            heartHalfOverlays[i].IsVisible = false;
            AddChild(heartHalfOverlays[i]);
        }

        // Create drumstick sprites (right side)
        for (var i = 0; i < MaxDrumsticks; i++)
        {
            var x = drumsticksStartX + i * IconSpacing;
            
            // Base drumstick sprite (shows empty or full)
            drumstickSprites[i] = CreateIconSprite(drumstickEmptyMaterial);
            drumstickSprites[i].SetPosition(new Vector2(x, statusY));
            AddChild(drumstickSprites[i]);

            // Half drumstick overlay
            drumstickHalfOverlays[i] = CreateIconSprite(drumstickHalfMaterial);
            drumstickHalfOverlays[i].SetPosition(new Vector2(x, statusY));
            drumstickHalfOverlays[i].IsVisible = false;
            AddChild(drumstickHalfOverlays[i]);
        }
    }

    private static Material CreateMaterial(string texturePath)
    {
        var (_, mat) = Sprite.CreateMeshAndMaterial(texturePath);
        return mat;
    }

    private static Sprite CreateIconSprite(Material material)
    {
        var (mesh, _) = Sprite.CreateMeshAndMaterial("Resources/gui/heart.png");
        var sprite = new Sprite(mesh, material)
        {
            Size = new Vector2i(ScaledIconSize),
            Pivot = Vector2.Zero
        };
        sprite.RenderGroup = RenderGroup.UI;
        sprite.DisableCulling = true;
        return sprite;
    }

    public override void OnUpdate(Scene scene, double elapsed)
    {
        // Always update on first call or when values change
        var healthChanged = lastHealth != attributes.Health;
        var foodChanged = lastFood != attributes.Food;
        
        if (!healthChanged && !foodChanged)
        {
            base.OnUpdate(scene, elapsed);
            return;
        }

        lastHealth = attributes.Health;
        lastFood = attributes.Food;

        UpdateHearts();
        UpdateDrumsticks();

        base.OnUpdate(scene, elapsed);
    }

    private void UpdateHearts()
    {
        // Health is 0-20 (half-hearts), we display 10 hearts
        var health = attributes.Health;

        for (var i = 0; i < MaxHearts; i++)
        {
            var heartValue = i * 2; // Each heart represents 2 HP

            if (health >= heartValue + 2)
            {
                // Full heart
                heartSprites[i].Material = heartFullMaterial;
                heartHalfOverlays[i].IsVisible = false;
            }
            else if (health >= heartValue + 1)
            {
                // Half heart - show empty base with half overlay
                heartSprites[i].Material = heartEmptyMaterial;
                heartHalfOverlays[i].IsVisible = true;
            }
            else
            {
                // Empty heart
                heartSprites[i].Material = heartEmptyMaterial;
                heartHalfOverlays[i].IsVisible = false;
            }
        }
    }

    private void UpdateDrumsticks()
    {
        // Food is 0-20 (half-drumsticks), we display 10 drumsticks
        var food = attributes.Food;

        for (var i = 0; i < MaxDrumsticks; i++)
        {
            var drumstickValue = i * 2;

            if (food >= drumstickValue + 2)
            {
                // Full drumstick
                drumstickSprites[i].Material = drumstickFullMaterial;
                drumstickHalfOverlays[i].IsVisible = false;
            }
            else if (food >= drumstickValue + 1)
            {
                // Half drumstick - show empty base with half overlay
                drumstickSprites[i].Material = drumstickEmptyMaterial;
                drumstickHalfOverlays[i].IsVisible = true;
            }
            else
            {
                // Empty drumstick
                drumstickSprites[i].Material = drumstickEmptyMaterial;
                drumstickHalfOverlays[i].IsVisible = false;
            }
        }
    }

    public void UpdatePosition(int screenWidth, int screenHeight)
    {
        var hotbarWidth = HotBar.Width;
        var hotbarY = screenHeight - HotBar.Height - 5;
        var statusY = hotbarY - ScaledIconSize - BarSpacing;

        var centerX = screenWidth / 2f;
        var heartsStartX = centerX - hotbarWidth / 2f;
        var drumsticksStartX = centerX + hotbarWidth / 2f - (MaxDrumsticks * IconSpacing);

        for (var i = 0; i < MaxHearts; i++)
        {
            var x = heartsStartX + i * IconSpacing;
            heartSprites[i].SetPosition(new Vector2(x, statusY));
            heartHalfOverlays[i].SetPosition(new Vector2(x, statusY));
        }

        for (var i = 0; i < MaxDrumsticks; i++)
        {
            var x = drumsticksStartX + i * IconSpacing;
            drumstickSprites[i].SetPosition(new Vector2(x, statusY));
            drumstickHalfOverlays[i].SetPosition(new Vector2(x, statusY));
        }
    }

    /// <summary>
    /// Gets or sets visibility for the status bar and all its children.
    /// </summary>
    public new bool IsVisible
    {
        get => heartSprites[0]?.IsVisible ?? false;
        set
        {
            foreach (var sprite in heartSprites)
            {
                sprite.IsVisible = value;
            }
            foreach (var sprite in drumstickSprites)
            {
                sprite.IsVisible = value;
            }
            // Half overlays are controlled by UpdateHearts/UpdateDrumsticks
            if (!value)
            {
                foreach (var overlay in heartHalfOverlays)
                {
                    overlay.IsVisible = false;
                }
                foreach (var overlay in drumstickHalfOverlays)
                {
                    overlay.IsVisible = false;
                }
            }
        }
    }
}
