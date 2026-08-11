using Franthropy.Dalamud.Observations;

namespace Franthropy.Dalamud.Tests.Observations;

public sealed class LatestByKeyBufferTests
{
    [Fact]
    public void Selling_surface_collector_outranks_capture_session_only_copies()
    {
        Assert.True(
            DalamudSharedObservationHost.SellingSurfaceWriterCapability >
            DalamudSharedObservationHost.CaptureSessionWriterCapability);
    }

    [Fact]
    public void Burst_schedules_one_write_and_preserves_the_newest_value_per_retainer()
    {
        var buffer = new LatestByKeyBuffer<ListingState>();

        Assert.True(buffer.Offer("Scrongle", new(2)));
        for (var quantity = 3; quantity <= 20; quantity++)
            Assert.False(buffer.Offer("Scrongle", new(quantity)));
        Assert.True(buffer.Offer("Eris-morne", new(0)));

        Assert.True(buffer.TryTake("Scrongle", out var scrongle));
        Assert.Equal(20, scrongle!.Quantity);
        Assert.True(buffer.TryTake("Eris-morne", out var eris));
        Assert.Equal(0, eris!.Quantity);
        Assert.False(buffer.TryTake("Scrongle", out _));
        Assert.True(buffer.Offer("Scrongle", new(21)));
    }

    private sealed record ListingState(int Quantity);
}
