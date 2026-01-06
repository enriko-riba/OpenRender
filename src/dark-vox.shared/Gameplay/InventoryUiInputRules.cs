namespace DarkVox.Shared.Gameplay;

public enum InventoryUiSlotKind
{
    None = 0,
    Inventory = 1,
    Crafting = 2,
    Result = 3,
}

public enum InventoryUiLeftPressAction
{
    None = 0,
    NormalClick = 1,
    BeginDistributePaint = 2,
    HotbarConfigureDrop = 3,
}

public static class InventoryUiInputRules
{
    public static InventoryUiLeftPressAction DecideLeftPressAction(
        bool heldItemIsEmpty,
        InventoryUiSlotKind hitKind,
        int hitIndex,
        InventoryUiSlotKind dragSourceKind,
        int dragSourceIndex)
    {
        if (hitKind is InventoryUiSlotKind.None or InventoryUiSlotKind.Result)
        {
            return InventoryUiLeftPressAction.None;
        }

        if (heldItemIsEmpty)
        {
            return InventoryUiLeftPressAction.NormalClick;
        }

        // Dragging from the hotbar should behave like a normal click-to-drop flow (swap/move).
        // It should not enter the paint/distribute flows and should not use the special
        // storage->hotbar configure logic.
        if (dragSourceKind == InventoryUiSlotKind.Inventory && dragSourceIndex >= 0 && dragSourceIndex < Inventory.HotbarSize)
        {
            return InventoryUiLeftPressAction.NormalClick;
        }

        // Storage -> hotbar is special: it configures the hotbar (1 item shortcut, uniqueness, etc).
        if (dragSourceKind == InventoryUiSlotKind.Inventory && dragSourceIndex >= Inventory.HotbarSize &&
            hitKind == InventoryUiSlotKind.Inventory && hitIndex >= 0 && hitIndex < Inventory.HotbarSize)
        {
            return InventoryUiLeftPressAction.HotbarConfigureDrop;
        }

        return InventoryUiLeftPressAction.BeginDistributePaint;
    }
}
