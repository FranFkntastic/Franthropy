using Franthropy.Dalamud.Diagnostics;

namespace Franthropy.Dalamud.Tests.Diagnostics;

public sealed class UiStateRecorderTests
{
    [Fact]
    public void RecordStateChange_StoresOnlyChangedState()
    {
        var recorder = new UiStateRecorder();
        var now = DateTimeOffset.UtcNow;
        recorder.Start("manual-desynthesis", now);

        Assert.True(recorder.RecordStateChange(now.AddMilliseconds(1), "framework", new Dictionary<string, string?> { ["addon"] = "selector", ["visible"] = "true" }));
        Assert.False(recorder.RecordStateChange(now.AddMilliseconds(2), "framework", new Dictionary<string, string?> { ["addon"] = "selector", ["visible"] = "true" }));
        Assert.True(recorder.RecordStateChange(now.AddMilliseconds(3), "framework", new Dictionary<string, string?> { ["addon"] = "dialog", ["visible"] = "true" }));

        var session = recorder.Stop(now.AddMilliseconds(4));
        var changes = session.Events.Where(value => value.Kind == UiStateEventKind.StateChanged).ToArray();
        Assert.Equal(2, changes.Length);
        Assert.Equal("dialog", changes[1].Details["addon"]);
    }

    [Fact]
    public void Recorder_IsBoundedAndReportsTruncation()
    {
        var recorder = new UiStateRecorder(100);
        var now = DateTimeOffset.UtcNow;
        recorder.Start("bounded", now);
        for (var index = 0; index < 150; index++)
            recorder.Record(now, UiStateEventKind.Marker, "test", index.ToString());

        var session = recorder.Stop(now);
        Assert.True(session.Truncated);
        Assert.Equal(100, session.Events.Count);
    }
}
