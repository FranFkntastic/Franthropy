using System.Reflection;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Automation.Retainers;

namespace Franthropy.Dalamud.Tests.Automation.Retainers;

public sealed class RetainerAutomationSessionTests
{
    [Theory]
    [InlineData("Entrust or withdraw items.", "Entrust or withdraw items", true)]
    [InlineData("Entrust or withdraw items. (22)", "Entrust or withdraw items", true)]
    [InlineData("\uE03CEntrust or withdraw items.", "Entrust or withdraw items", true)]
    [InlineData("Assign venture.", "Entrust or withdraw items", false)]
    public void SelectStringMatch_NormalizesDecoratedLocalizedEntries(string entry, string target, bool expected) =>
        Assert.Equal(expected, RetainerUiAutomationText.IsSelectStringEntryMatch(entry, target));

    [Fact]
    public void RetainerSelection_RequiresActiveMatchingRow()
    {
        var rows = new[]
        {
            new RetainerListEntry("Alpha", true),
            new RetainerListEntry("Beta", false),
            new RetainerListEntry("Gamma", true),
        };

        Assert.Equal(2, RetainerUiAutomationText.FindRetainerListIndex(rows, "gamma"));
        Assert.Null(RetainerUiAutomationText.FindRetainerListIndex(rows, "Beta"));
    }

    [Theory]
    [InlineData(100, 10, 4, 100, 6, 3, 7, true)]
    [InlineData(100, 10, 10, 0, 0, 3, 13, true)]
    [InlineData(100, 10, 4, 100, 6, 3, 6, false)]
    [InlineData(100, 10, 4, 100, 7, 3, 7, false)]
    public void RetrievalObservation_RequiresMatchingSourceAndDestinationDeltas(
        uint itemId,
        int original,
        int transferred,
        uint observedItemId,
        int observedQuantity,
        int playerBefore,
        int playerAfter,
        bool expected) =>
        Assert.Equal(expected, RetainerRetrievalObservation.Matches(
            itemId,
            original,
            transferred,
            observedItemId,
            observedQuantity,
            playerBefore,
            playerAfter));

    [Fact]
    public async Task Session_PropagatesCancellationIntoFrameworkWork()
    {
        using var cancellation = new CancellationTokenSource();
        CancellationToken observed = default;
        var framework = CreateProxy<IFramework>((method, arguments) =>
        {
            Assert.Equal(nameof(IFramework.RunOnTick), method.Name);
            observed = arguments!.OfType<CancellationToken>().Single();
            return CreateCancellableTask(method.ReturnType, observed);
        });
        var unused = new Func<MethodInfo, object?[]?, object?>((method, _) =>
            throw new InvalidOperationException($"Unexpected dependency call: {method.Name}."));
        var session = new DalamudRetainerAutomationSession(
            framework,
            CreateProxy<IGameGui>(unused),
            CreateProxy<IDataManager>(unused),
            CreateProxy<IPluginLog>(unused),
            CreateProxy<IObjectTable>(unused),
            CreateProxy<ITargetManager>(unused),
            CreateProxy<ISigScanner>(unused));

        var open = session.OpenInventoryAsync(cancellation.Token);

        Assert.Equal(cancellation.Token, observed);
        Assert.False(open.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static T CreateProxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, ConfigurableDispatchProxy>();
        ((ConfigurableDispatchProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private static object CreateCancellableTask(Type taskType, CancellationToken cancellationToken)
    {
        var resultType = taskType.GetGenericArguments().Single();
        return typeof(RetainerAutomationSessionTests)
            .GetMethod(nameof(CreateCancellableTaskCore), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(resultType)
            .Invoke(null, [cancellationToken])!;
    }

    private static async Task<T> CreateCancellableTaskCore<T>(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return default!;
    }

    public class ConfigurableDispatchProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }
}
