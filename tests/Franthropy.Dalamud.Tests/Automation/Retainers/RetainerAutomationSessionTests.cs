using System.Reflection;
using System.Reflection.Emit;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Franthropy.Dalamud.Automation.Inventory;
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
    [InlineData(999, 19980, 18981, 11996, 12995, true)]
    [InlineData(999, 19980, 19980, 11996, 12995, false)]
    [InlineData(999, 19980, 18981, 11996, 11996, false)]
    [InlineData(999, 19980, 17982, 11996, 12995, false)]
    public void RetrievalObservation_AggregateFallbackRequiresExactTwoSidedMovement(
        int transferred,
        int retainerBefore,
        int retainerAfter,
        int playerBefore,
        int playerAfter,
        bool expected) =>
        Assert.Equal(expected, RetainerRetrievalObservation.MatchesAggregate(
            transferred,
            retainerBefore,
            retainerAfter,
            playerBefore,
            playerAfter));

    [Theory]
    [InlineData(999, -999, 999, true)]
    [InlineData(999, 0, 999, false)]
    [InlineData(999, -999, 0, false)]
    [InlineData(999, -1998, 999, false)]
    public void RetrievalObservation_CommandEventsRequireExactTwoSidedMovement(
        int transferred,
        int retainerDelta,
        int playerDelta,
        bool expected) =>
        Assert.Equal(expected, RetainerRetrievalObservation.MatchesMutation(
            transferred,
            retainerDelta,
            playerDelta));

    [Fact]
    public void RetrievalMutationAccumulator_IgnoresBalancedSlotRearrangement()
    {
        var evidence = new RetainerRetrievalMutationAccumulator();

        evidence.RecordRetainer(-999);
        evidence.RecordRetainer(999);
        evidence.RecordRetainer(-999);
        evidence.RecordPlayer(999);

        Assert.True(evidence.Matches(999));
        Assert.Equal(new(-999, 999), evidence.Snapshot());
    }

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

    [Theory]
    [InlineData(null, 1u, false)]
    [InlineData(0u, 1u, false)]
    [InlineData(44u, 0u, false)]
    [InlineData(44u, 44u, false)]
    [InlineData(44u, 45u, true)]
    [InlineData(44u, 999_999_999u, true)]
    [InlineData(44u, 1_000_000_000u, false)]
    public void MarketPricePolicy_RequiresAValidChangedLivePrice(
        uint? observedUnitPrice,
        uint requestedUnitPrice,
        bool expected) =>
        Assert.Equal(
            expected,
            RetainerMarketPricePolicy.IsValidMutation(observedUnitPrice, requestedUnitPrice));

    [Fact]
    public void MarketPriceUpdatePolicy_RejectsAnyUnownedConfirmation()
    {
        var confirmation = RetainerMarketPriceUpdatePolicy.Decide(false, true);
        var waiting = RetainerMarketPriceUpdatePolicy.Decide(false, false);
        var committed = RetainerMarketPriceUpdatePolicy.Decide(true, false);
        var committedWithConfirmation = RetainerMarketPriceUpdatePolicy.Decide(true, true);

        Assert.Equal(RetainerMarketPriceUpdateAction.RejectUnexpectedConfirmation, confirmation);
        Assert.Equal(RetainerMarketPriceUpdateAction.Wait, waiting);
        Assert.Equal(RetainerMarketPriceUpdateAction.Complete, committed);
        Assert.Equal(RetainerMarketPriceUpdateAction.RejectUnexpectedConfirmation, committedWithConfirmation);
    }

    [Fact]
    public void MarketListingPostResult_DistinguishesPreSendCommittedAndIndeterminateOutcomes()
    {
        var listing = new RetainerMarketListingTarget(4, 5333, 1, false, 1_234_581);

        var failed = RetainerMarketListingPostResult.Failed("NoSend", "Nothing was sent.");
        var committed = RetainerMarketListingPostResult.Succeeded(listing);
        var indeterminate = RetainerMarketListingPostResult.Indeterminate(listing, "Unknown", "Re-scan.");

        Assert.Equal(RetainerMarketListingPostOutcome.FailedBeforeSend, failed.Outcome);
        Assert.False(failed.Success);
        Assert.False(failed.RequestSent);
        Assert.Equal(RetainerMarketListingPostOutcome.Committed, committed.Outcome);
        Assert.True(committed.Success);
        Assert.True(committed.RequestSent);
        Assert.Equal(RetainerMarketListingPostOutcome.Indeterminate, indeterminate.Outcome);
        Assert.False(indeterminate.Success);
        Assert.True(indeterminate.RequestSent);
        Assert.Equal(listing, indeterminate.Listing);
    }

    [Fact]
    public async Task PriceUpdate_RejectsMissingObservedPriceBeforeTouchingFrameworkState()
    {
        var session = CreateSession(
            "2026.07.16.0001.0000",
            (method, _) => throw new InvalidOperationException($"Unexpected dependency call: {method.Name}."));

        var result = await session.UpdateSellingListingPriceAsync(
            new RetainerMarketListingTarget(4, 5333, 1, false, null),
            1_234_581);

        Assert.False(result.Success);
        Assert.Equal("InvalidObservedMarketUnitPrice", result.Code);
    }

    [Fact]
    public async Task MarketListingPost_RejectsUnsupportedBuildBeforeTouchingFrameworkState()
    {
        var session = CreateSession(
            "unsupported-build",
            (method, _) => throw new InvalidOperationException($"Unexpected dependency call: {method.Name}."));
        var source = new DalamudInventoryStack(InventoryType.Inventory1, 0, 5333, 1);

        var result = await session.PostMarketListingAsync(source, 1, 1_234_581);

        Assert.Equal(RetainerMarketListingPostOutcome.FailedBeforeSend, result.Outcome);
        Assert.False(result.RequestSent);
        Assert.Equal("UnsupportedGameBuild", result.Code);
    }

    [Fact]
    public void MarketListingObservation_RequiresExactPostedIdentityAndPrice()
    {
        var expected = new RetainerMarketListingTarget(4, 5333, 1, false, 1_234_581);
        var missingPrice = expected with { UnitPrice = null };

        Assert.True(RetainerMarketListingObservation.Matches(expected, 5333, 1, false, 1_234_581));
        Assert.False(RetainerMarketListingObservation.Matches(expected, 5333, 2, false, 1_234_581));
        Assert.False(RetainerMarketListingObservation.Matches(expected, 5333, 1, true, 1_234_581));
        Assert.False(RetainerMarketListingObservation.Matches(expected, 5333, 1, false, 1_234_580));
        Assert.False(RetainerMarketListingObservation.Matches(missingPrice, 5333, 1, false, 1_234_581));
    }

    [Theory]
    [InlineData(4, false, 1_234_581, true)]
    [InlineData(5, false, 1_234_581, false)]
    [InlineData(4, true, 1_234_581, false)]
    [InlineData(4, false, 1_234_580, false)]
    public void MarketPriceCommit_RequiresTheReconciledExactSlotAndClosedEditor(
        int observedSlotIndex,
        bool listingEditorReady,
        ulong observedUnitPrice,
        bool expected)
    {
        var listing = new RetainerMarketListingTarget(4, 5333, 1, false, 1_234_581);

        Assert.Equal(expected, RetainerMarketPriceCommitObservation.Matches(
            listing,
            observedSlotIndex,
            5333,
            1,
            false,
            observedUnitPrice,
            listingEditorReady));
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

    private static DalamudRetainerAutomationSession CreateSession(
        string currentGameVersion,
        Func<MethodInfo, object?[]?, object?> dependencyHandler)
    {
        return new(
            CreateProxy<IFramework>(dependencyHandler),
            CreateProxy<IGameGui>(dependencyHandler),
            CreateProxy<IDataManager>(dependencyHandler),
            CreateProxy<IPluginLog>(dependencyHandler),
            CreateProxy<IObjectTable>(dependencyHandler),
            CreateProxy<ITargetManager>(dependencyHandler),
            CreateProxy<ISigScanner>(dependencyHandler),
            currentGameVersion);
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
