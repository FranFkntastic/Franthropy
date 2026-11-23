using Franthropy.Dalamud.Automation;

namespace Franthropy.Dalamud.Tests.Automation;

public sealed class DalamudExternalUiAutomationSuppressionTests
{
    private sealed class Store : ISharedPluginDataStore
    {
        public readonly HashSet<string> YesAlready = [];
        public readonly HashSet<string> TextAdvance = [];

        public bool TryGetData<T>(string key, out T? data) where T : class
        {
            data = key switch
            {
                "YesAlready.StopRequests" => YesAlready as T,
                "TextAdvance.StopRequests" => TextAdvance as T,
                _ => null,
            };
            return data != null;
        }
    }

    private sealed class Harness
    {
        public readonly Store Store = new();
        public readonly List<string> Warnings = [];
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public readonly DalamudExternalUiAutomationSuppression Suppression;

        public Harness() => Suppression = new(Store, _ => { }, "Owner", Warnings.Add, () => Now);
    }

    [Fact]
    public void PlainHoldNeverExpires()
    {
        var h = new Harness();
        using var scope = h.Suppression.Acquire();
        h.Now += TimeSpan.FromDays(1);

        Assert.Equal(0, h.Suppression.ReleaseExpired());
        Assert.True(scope.IsHeld);
        Assert.True(scope.Renew());
        Assert.Contains("Owner", h.Store.YesAlready);
        Assert.Contains("Owner", h.Store.TextAdvance);
        Assert.Empty(h.Warnings);
    }

    [Fact]
    public void RenewedHeartbeatHoldStaysHeld()
    {
        var h = new Harness();
        using var scope = h.Suppression.Acquire(TimeSpan.FromMinutes(1));
        for (var beat = 0; beat < 30; beat++)
        {
            h.Now += TimeSpan.FromSeconds(50);
            Assert.True(scope.Renew());
            Assert.Equal(0, h.Suppression.ReleaseExpired());
        }
        Assert.True(scope.IsHeld);
        Assert.Contains("Owner", h.Store.YesAlready);
    }

    [Fact]
    public void StalledHeartbeatHoldIsDroppedWithWarning()
    {
        var h = new Harness();
        var live = h.Suppression.Acquire();
        var stalled = h.Suppression.Acquire(TimeSpan.FromMinutes(1));
        h.Now += TimeSpan.FromMinutes(2);

        Assert.Equal(1, h.Suppression.ReleaseExpired());
        Assert.False(stalled.IsHeld);
        Assert.False(stalled.Renew());
        Assert.True(live.IsHeld);
        Assert.Contains("Owner", h.Store.YesAlready); // the live hold keeps the pause
        Assert.Contains(h.Warnings, warning => warning.Contains("SuppressionHeartbeatExpired"));

        live.Dispose();
        Assert.DoesNotContain("Owner", h.Store.YesAlready);
    }

    [Fact]
    public void ForceReleaseDropsEveryHolder()
    {
        var h = new Harness();
        var first = h.Suppression.Acquire();
        var second = h.Suppression.Acquire(TimeSpan.FromMinutes(1));

        Assert.Equal(2, h.Suppression.ForceRelease("test"));
        Assert.False(first.IsHeld);
        Assert.False(second.Renew());
        Assert.DoesNotContain("Owner", h.Store.YesAlready);
        Assert.DoesNotContain("Owner", h.Store.TextAdvance);
        first.Dispose();
        Assert.Equal(0, h.Suppression.HolderCount);
        Assert.Contains(h.Warnings, warning => warning.Contains("SuppressionForceReleased"));
    }
}
