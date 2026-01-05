namespace DarkVox.Shared.Gameplay;

/// <summary>
/// Small helpers to keep inventory/crafting interactions Minecraft-like.
/// This is intentionally UI-agnostic so it can be unit tested.
/// </summary>
public static class MinecraftUiRules
{
    /// <summary>
    /// Right-click picking up a stack splits it in half (rounded up).
    /// Examples: 1->1, 2->1, 3->2, 4->2.
    /// </summary>
    public static int SplitHalfRoundedUp(int count) => count <= 0 ? 0 : (count + 1) / 2;
}
