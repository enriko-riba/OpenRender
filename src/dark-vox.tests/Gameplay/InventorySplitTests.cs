using DarkVox.Shared.Gameplay;
using Xunit;

namespace DarkVox.Tests.Gameplay;

public sealed class InventorySplitTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(63, 32)]
    [InlineData(64, 32)]
    public void RightClickSplit_ShouldPickUpHalfRoundedUp(int stackCount, int expectedHeld)
    {
        Assert.Equal(expectedHeld, MinecraftUiRules.SplitHalfRoundedUp(stackCount));
    }
}
