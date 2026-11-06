using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.Automation.Ui;

namespace Franthropy.Dalamud.Tests.Automation;

public unsafe class DalamudNumericDialogTests
{
    [Fact]
    public void AbsentOrLoadingPopupIsPending()
    {
        var addon = new AtkUnitBase();
        Assert.Equal(NumericDialogSubmission.Pending, DalamudNumericDialog.TrySubmit(null, 2));
        Assert.Equal(NumericDialogSubmission.Pending, DalamudNumericDialog.TrySubmit(&addon, 2));
    }

    [Theory]
    [InlineData(1u, 99u, true)]
    [InlineData(0u, 0u, true)]
    [InlineData(5u, 4u, false)]
    [InlineData(0u, uint.MaxValue, false)]
    public void ReadsBoundsWithoutClampingOrSubmitting(uint min, uint max, bool available)
    {
        var values = stackalloc AtkValue[4];
        values[2].UInt = min;
        values[3].UInt = max;
        var addon = new AtkUnitBase { AtkValues = values, AtkValuesCount = 4, Flags1A1 = 1, IsVisible = true };
        Assert.Equal(available, DalamudNumericDialog.TryGetBounds(&addon, out var minimum, out var maximum));
        if (available)
        {
            Assert.Equal((int)min, minimum);
            Assert.Equal((int)max, maximum);
        }
        addon.IsVisible = false;
        Assert.False(DalamudNumericDialog.TryGetBounds(&addon, out _, out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4)]
    public void OutOfRangeDoesNotCallNativeSubmission(int quantity)
    {
        var values = stackalloc AtkValue[4];
        values[2].UInt = 1;
        values[3].UInt = 3;
        var addon = new AtkUnitBase { AtkValues = values, AtkValuesCount = 4, Flags1A1 = 1, IsVisible = true };
        Assert.Equal(NumericDialogSubmission.OutOfRange, DalamudNumericDialog.TrySubmit(&addon, quantity));
        addon.AtkValuesCount = 3;
        Assert.Equal(NumericDialogSubmission.Pending, DalamudNumericDialog.TrySubmit(&addon, quantity));
        addon.AtkValuesCount = 4;
        addon.AtkValues = null;
        Assert.Equal(NumericDialogSubmission.Pending, DalamudNumericDialog.TrySubmit(&addon, quantity));
    }
}
