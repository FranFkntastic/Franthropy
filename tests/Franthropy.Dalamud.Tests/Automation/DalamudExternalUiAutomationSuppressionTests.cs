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
}
