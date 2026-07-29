using System.Reflection;
using System.Reflection.Emit;
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

    [Fact]
    public void FirstRetainerSelection_SkipsInactiveAndEmptyRows()
    {
        var rows = new[]
        {
            new RetainerListEntry("Inactive", false),
            new RetainerListEntry(string.Empty, true),
            new RetainerListEntry("First active", true),
            new RetainerListEntry("Second active", true),
        };

        Assert.Equal(2, RetainerUiAutomationText.FindFirstActiveRetainerListIndex(rows));
        Assert.Null(RetainerUiAutomationText.FindFirstActiveRetainerListIndex(
            [new RetainerListEntry("Inactive", false)]));
    }

    [Theory]
    [InlineData(true, true, true, 42, 42, false, (int)RetainerOpeningAction.Complete)]
    [InlineData(false, true, false, 42, 42, false, (int)RetainerOpeningAction.AdvanceTalk)]
    [InlineData(false, true, false, 42, 99, false, (int)RetainerOpeningAction.RejectIdentity)]
    [InlineData(false, true, false, 0, 42, false, (int)RetainerOpeningAction.Wait)]
    [InlineData(false, false, true, 0, 42, false, (int)RetainerOpeningAction.Wait)]
    [InlineData(false, false, true, 0, 42, true, (int)RetainerOpeningAction.CompleteAtList)]
    public void RetainerOpening_AdvancesOnlyVerifiedTalk(
        bool menuReady,
        bool talkReady,
        bool listReady,
        ulong activeRetainerId,
        int expectedRetainerId,
        bool allowRetainerListCompletion,
        int expected)
    {
        var observed = new RetainerOpeningObservation(
            menuReady,
            talkReady,
            listReady,
            activeRetainerId);

        Assert.Equal(
            (RetainerOpeningAction)expected,
            RetainerOpeningPolicy.Decide(
                observed,
                checked((ulong)expectedRetainerId),
                allowRetainerListCompletion));
    }

    [Fact]
    public void RetainerOpening_AllowsVerifiedCurrentRetainerWithoutAnExpectedIdentity()
    {
        var observed = new RetainerOpeningObservation(
            CommandMenuReady: false,
            TalkReady: true,
            RetainerListReady: false,
            ActiveRetainerId: 42);

        Assert.Equal(RetainerOpeningAction.AdvanceTalk, RetainerOpeningPolicy.Decide(observed, null));
    }

    [Theory]
    [InlineData(true, false, 0, 42, (int)RetainerClosingAction.Complete)]
    [InlineData(false, true, 42, 42, (int)RetainerClosingAction.AdvanceTalk)]
    [InlineData(false, true, 0, 42, (int)RetainerClosingAction.AdvanceTalk)]
    [InlineData(false, true, 99, 42, (int)RetainerClosingAction.RejectIdentity)]
    [InlineData(false, false, 42, 42, (int)RetainerClosingAction.Wait)]
    public void RetainerClosing_AdvancesOnlyTheExpectedFarewell(
        bool listReady,
        bool talkReady,
        ulong activeRetainerId,
        ulong expectedRetainerId,
        int expected)
    {
        var observed = new RetainerClosingObservation(
            listReady,
            talkReady,
            activeRetainerId);

        Assert.Equal(
            (RetainerClosingAction)expected,
            RetainerClosingPolicy.Decide(observed, expectedRetainerId));
    }

    [Fact]
    public void NativeRetainerRowReader_DoesNotCallItself()
    {
        var method = typeof(DalamudRetainerAutomationSession).GetMethod(
            "ReadRetainerListEntries",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var il = method.GetMethodBody()!.GetILAsByteArray()!;

        for (var index = 0; index <= il.Length - 5; index++)
        {
            var recursivelyCallsItself =
                il[index] == (byte)OpCodes.Call.Value &&
                BitConverter.ToInt32(il, index + 1) == method.MetadataToken;
            Assert.False(recursivelyCallsItself);
        }
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

    [Theory]
    [InlineData(false, 999, 400, 3, true)]
    [InlineData(false, 400, 400, 0, false)]
    [InlineData(true, 9999, 7000, 0, true)]
    [InlineData(true, 7000, 7000, 0, true)]
    public void RetrievalCommand_UsesTypedPartialAndCrystalSemantics(
        bool isCrystalContainer,
        int sourceQuantity,
        int requestedQuantity,
        long expectedCommand,
        bool expectedQuantityInput)
    {
        var selected = RetainerRetrievalCommandPolicy.Select(isCrystalContainer, sourceQuantity, requestedQuantity);

        Assert.Equal(expectedCommand, (long)selected.Command);
        Assert.Equal(expectedQuantityInput, selected.NeedsQuantityInput);
    }

    [Theory]
    [InlineData(4, 10, 6, 3, 7, true)]
    [InlineData(4, 10, 7, 3, 7, false)]
    [InlineData(4, 10, 6, 3, 6, false)]
    [InlineData(0, 10, 10, 3, 3, false)]
    public void DepositObservation_RequiresMatchingSourceAndDestinationDeltas(
        int transferred,
        int playerBefore,
        int playerAfter,
        int retainerBefore,
        int retainerAfter,
        bool expected) =>
        Assert.Equal(expected, RetainerDepositObservation.Matches(
            transferred,
            playerBefore,
            playerAfter,
            retainerBefore,
            retainerAfter));

    [Theory]
    [InlineData(100, 2, false, 44, true)]
    [InlineData(101, 2, false, 44, false)]
    [InlineData(100, 3, false, 44, false)]
    [InlineData(100, 2, true, 44, false)]
    [InlineData(100, 2, false, 45, false)]
    public void MarketListingObservation_RequiresTheCompletePhysicalListingIdentity(
        uint observedItemId,
        int observedQuantity,
        bool observedIsHq,
        ulong observedUnitPrice,
        bool expected)
    {
        var listing = new RetainerMarketListingTarget(3, 100, 2, false, 44);

        Assert.Equal(expected, RetainerMarketListingObservation.Matches(
            listing,
            observedItemId,
            observedQuantity,
            observedIsHq,
            observedUnitPrice));
    }

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
            CreateProxy<ISigScanner>(unused),
            "2026.07.16.0001.0000");

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
