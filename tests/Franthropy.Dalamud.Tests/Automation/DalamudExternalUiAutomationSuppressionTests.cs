using Franthropy.Dalamud.Automation;

namespace Franthropy.Dalamud.Tests.Automation;

public sealed class DalamudExternalUiAutomationSuppressionTests
{
    [Fact]
    public void Nested_scopes_add_and_remove_only_one_owner_entry()
    {
        var textAdvance = new HashSet<string>(StringComparer.Ordinal) { "OtherPlugin" };
        var yesAlready = new HashSet<string>(StringComparer.Ordinal);
        var store = new FakeStore(textAdvance, yesAlready);
        using var suppression = new DalamudExternalUiAutomationSuppression(store, _ => { }, "Quartermaster");

        var first = suppression.Acquire();
        var second = suppression.Acquire();

        Assert.Equal(["OtherPlugin", "Quartermaster"], textAdvance.Order(StringComparer.Ordinal));
        Assert.Equal(["Quartermaster"], yesAlready);

        first.Dispose();
        Assert.Contains("Quartermaster", textAdvance);
        second.Dispose();

        Assert.Equal(["OtherPlugin"], textAdvance);
        Assert.Empty(yesAlready);
    }

    [Fact]
    public void Existing_owner_entry_is_never_removed()
    {
        var textAdvance = new HashSet<string>(StringComparer.Ordinal) { "Quartermaster" };
        var store = new FakeStore(textAdvance, null);
        using var suppression = new DalamudExternalUiAutomationSuppression(store, _ => { }, "Quartermaster");

        using (suppression.Acquire())
            Assert.Contains("Quartermaster", textAdvance);

        Assert.Contains("Quartermaster", textAdvance);
    }

    private sealed class FakeStore(HashSet<string>? textAdvance, HashSet<string>? yesAlready) : ISharedPluginDataStore
    {
        public bool TryGetData<T>(string key, out T? data)
            where T : class
        {
            object? value = key switch
            {
                "TextAdvance.StopRequests" => textAdvance,
                "YesAlready.StopRequests" => yesAlready,
                _ => null,
            };
            data = value as T;
            return data is not null;
        }
    }
}
