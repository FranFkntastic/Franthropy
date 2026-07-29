using Franthropy.Dalamud.Automation.Vendors;

namespace Franthropy.Dalamud.Tests.Automation.Vendors;

public sealed class GilVendorAetheryteSnapshotTests
{
    [Fact]
    public void TryRead_ReturnsOnlyTheCurrentOwnersObservation()
    {
        var snapshot = new GilVendorAetheryteSnapshot();
        snapshot.Observe(100, [8, 75]);

        Assert.True(snapshot.TryRead(100, out var firstOwner));
        Assert.Equal([8u, 75u], firstOwner.Order());

        Assert.False(snapshot.TryRead(200, out var secondOwner));
        Assert.Empty(secondOwner);
    }

    [Fact]
    public void Observe_DropsZeroAndReplacesThePreviousTruthAtomically()
    {
        var snapshot = new GilVendorAetheryteSnapshot();
        snapshot.Observe(100, [8, 0, 75]);
        snapshot.Observe(100, [24]);

        Assert.True(snapshot.TryRead(100, out var observed));
        Assert.Equal([24u], observed);
    }
}
