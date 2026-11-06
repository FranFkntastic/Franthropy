using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Franthropy.Dalamud.Automation.Ui;

public enum NumericDialogSubmission { Pending, OutOfRange, Submitted }

/// <summary>
/// Numeric popup mechanics only. Callers establish dialog ownership and choose
/// the exact quantity; submission is not evidence that the operation completed.
/// Use on the framework thread and do not retain addon pointers across frames.
/// </summary>
public static unsafe class DalamudNumericDialog
{
    public static bool TryGetBounds(AtkUnitBase* addon, out int minimum, out int maximum)
    {
        minimum = maximum = 0;
        if (addon == null || !addon->IsReady || !addon->IsVisible ||
            addon->AtkValues == null || addon->AtkValuesCount <= 3)
            return false;
        var min = addon->AtkValues[2].UInt;
        var max = addon->AtkValues[3].UInt;
        if (min > max || max > int.MaxValue)
            return false;
        minimum = (int)min;
        maximum = (int)max;
        return true;
    }

    public static NumericDialogSubmission TrySubmit(AtkUnitBase* addon, int quantity)
    {
        if (!TryGetBounds(addon, out var minimum, out var maximum))
            return NumericDialogSubmission.Pending;
        if (quantity < minimum || quantity > maximum)
            return NumericDialogSubmission.OutOfRange;
        addon->FireCallbackInt(quantity);
        return NumericDialogSubmission.Submitted;
    }
}
