using DarkVox.Shared.Gameplay;
using Xunit;

namespace DarkVox.Tests.Inventory;

public class InventoryUiInputRulesTests
{
    [Fact]
    public void DecideLeftPressAction_WhenHoldingAndHotbarSlot_ShouldBypassPaint()
    {
        var action = InventoryUiInputRules.DecideLeftPressAction(
            heldItemIsEmpty: false,
            hitKind: InventoryUiSlotKind.Inventory,
            hitIndex: 0,
            dragSourceKind: InventoryUiSlotKind.Inventory,
            dragSourceIndex: DarkVox.Shared.Gameplay.Inventory.HotbarSize);

        Assert.Equal(InventoryUiLeftPressAction.HotbarConfigureDrop, action);
    }

    [Fact]
    public void DecideLeftPressAction_WhenHoldingAndStorageSlot_ShouldStartDistributePaint()
    {
        var action = InventoryUiInputRules.DecideLeftPressAction(
            heldItemIsEmpty: false,
            hitKind: InventoryUiSlotKind.Inventory,
            hitIndex: DarkVox.Shared.Gameplay.Inventory.HotbarSize,
            dragSourceKind: InventoryUiSlotKind.Inventory,
            dragSourceIndex: DarkVox.Shared.Gameplay.Inventory.HotbarSize);

        Assert.Equal(InventoryUiLeftPressAction.BeginDistributePaint, action);
    }

    [Fact]
    public void DecideLeftPressAction_WhenNotHolding_ShouldBeNormalClick()
    {
        var action = InventoryUiInputRules.DecideLeftPressAction(
            heldItemIsEmpty: true,
            hitKind: InventoryUiSlotKind.Inventory,
            hitIndex: 0,
            dragSourceKind: InventoryUiSlotKind.None,
            dragSourceIndex: -1);

        Assert.Equal(InventoryUiLeftPressAction.NormalClick, action);
    }

    [Fact]
    public void DecideLeftPressAction_WhenDraggingFromHotbar_ShouldBeNormalClick()
    {
        var action = InventoryUiInputRules.DecideLeftPressAction(
            heldItemIsEmpty: false,
            hitKind: InventoryUiSlotKind.Inventory,
            hitIndex: DarkVox.Shared.Gameplay.Inventory.HotbarSize,
            dragSourceKind: InventoryUiSlotKind.Inventory,
            dragSourceIndex: 0);

        Assert.Equal(InventoryUiLeftPressAction.NormalClick, action);
    }
}
