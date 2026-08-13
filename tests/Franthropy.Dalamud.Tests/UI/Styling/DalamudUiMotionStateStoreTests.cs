using Franthropy.Dalamud.UI.Styling;

namespace Franthropy.Dalamud.Tests.UI.Styling;

public sealed class DalamudUiMotionStateStoreTests
{
    [Fact]
    public void Static_transition_reaches_target_immediately()
    {
        var store = new DalamudUiMotionStateStore();
        store.BeginFrame();

        Assert.Equal(1f, store.Track("button", true, 0f, 0f));
        Assert.Equal(0f, store.Track("button", false, 0f, 0f));
    }

    [Fact]
    public void Timed_transition_is_deterministic_and_bounded()
    {
        var store = new DalamudUiMotionStateStore();
        store.BeginFrame();
        store.Track("tab", false, 0f, 0f);

        Assert.Equal(0.25f, store.Track("tab", true, 0.025f, 0.1f), 3);
        Assert.Equal(0.75f, store.Track("tab", true, 0.05f, 0.1f), 3);
        Assert.Equal(1f, store.Track("tab", true, 0.05f, 0.1f));
    }

    [Fact]
    public void Prune_removes_only_entries_idle_beyond_the_frame_budget()
    {
        var store = new DalamudUiMotionStateStore();
        store.BeginFrame();
        store.Track("old", true, 0f, 0f);
        store.BeginFrame();
        store.Track("current", true, 0f, 0f);
        store.BeginFrame();

        Assert.Equal(1, store.Prune(1));
        Assert.Equal(1, store.Count);
    }
}
