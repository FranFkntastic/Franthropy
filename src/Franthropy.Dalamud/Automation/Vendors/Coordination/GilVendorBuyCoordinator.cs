using Franthropy.Dalamud.Automation.Vendors;

namespace Franthropy.Dalamud.Automation.Vendors.Coordination;

public sealed class GilVendorBuyCoordinator : IDisposable
{
    private static readonly TimeSpan ReceiptTimeout = TimeSpan.FromSeconds(4);
    private readonly IGilVendorBuyRunStore store;
    private readonly IGilVendorBuyRuntime runtime;
    private readonly Func<DateTimeOffset> utcNow;
    private GilVendorBuyFallbackReplanner? fallbackReplanner;
    private bool disposed;

    public GilVendorBuyCoordinator(
        IGilVendorBuyRunStore store,
        IGilVendorBuyRuntime runtime,
        Func<DateTimeOffset>? utcNow = null,
        GilVendorBuyFallbackReplanner? fallbackReplanner = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.fallbackReplanner = fallbackReplanner;
        ActiveRun = store.LoadCurrent();
        if (IsRunning)
            runtime.BeginAutomation();
    }

    public GilVendorBuyRunSnapshot? ActiveRun { get; private set; }

    public bool IsRunning => ActiveRun?.Phase is
        GilVendorBuyPhase.RefreshPreconditions or
        GilVendorBuyPhase.ReachVendor or
        GilVendorBuyPhase.ValidateShop or
        GilVendorBuyPhase.PurchaseLine or
        GilVendorBuyPhase.VerifyReceipt;

    public bool TryStart(GilVendorBuyPlan plan, string contextSignature, out string error)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(contextSignature);
        if (IsRunning || ActiveRun?.Phase == GilVendorBuyPhase.Paused)
        {
            error = "A vendor buy run is already active.";
            return false;
        }
        if (plan.Lines.Count == 0 || plan.Stops.Count == 0)
        {
            error = "The vendor buy plan is empty.";
            return false;
        }

        var quantities = plan.Lines
            .Where(line => line.ApprovedQuantity > 0)
            .ToDictionary(line => line.ItemId, line => line.ApprovedQuantity);
        if (quantities.Count == 0)
        {
            error = "The vendor buy plan is empty.";
            return false;
        }
        var preflight = runtime.CaptureInventory(quantities.Keys.ToArray());
        if (!preflight.IsComplete || preflight.Gil is null)
        {
            error = preflight.Message;
            return false;
        }
        if (preflight.Gil.Value < plan.MaximumApprovedGil)
        {
            error = $"The vendor plan requires up to {plan.MaximumApprovedGil:N0} gil, but only {preflight.Gil.Value:N0} gil is available.";
            return false;
        }
        if (!runtime.HasCapacity(quantities, out error))
            return false;

        var now = utcNow().UtcDateTime;
        ActiveRun = new GilVendorBuyRunSnapshot
        {
            RunId = Guid.NewGuid().ToString("N"),
            ContextSignature = contextSignature,
            MaximumApprovedGil = plan.MaximumApprovedGil,
            Phase = GilVendorBuyPhase.RefreshPreconditions,
            ResumePhase = GilVendorBuyPhase.RefreshPreconditions,
            Message = "Vendor buy started.",
            StartedAtUtc = now,
            UpdatedAtUtc = now,
            Lines = plan.Lines.Select(CloneLine).ToList(),
            Stops = plan.Stops.Select(CloneStop).ToList(),
        };
        fallbackReplanner = plan.FallbackReplanner ?? fallbackReplanner;
        runtime.BeginAutomation();
        Persist();
        error = string.Empty;
        return true;
    }

    public void Tick(string currentContextSignature)
    {
        if (disposed || ActiveRun is not { } run || !IsRunning)
            return;
        if (!string.Equals(run.ContextSignature, currentContextSignature, StringComparison.Ordinal))
        {
            Pause("The current context does not match the frozen vendor buy plan.");
            return;
        }

        switch (run.Phase)
        {
            case GilVendorBuyPhase.RefreshPreconditions: TickRefreshPreconditions(run); break;
            case GilVendorBuyPhase.ReachVendor: TickReachVendor(run); break;
            case GilVendorBuyPhase.ValidateShop: TickValidateShop(run); break;
            case GilVendorBuyPhase.PurchaseLine: TickPurchaseLine(run); break;
            case GilVendorBuyPhase.VerifyReceipt: TickVerifyReceipt(run); break;
        }
    }

    public bool Pause(string message = "Vendor buy paused.")
    {
        if (ActiveRun is not { } run || !IsRunning)
            return false;
        run.ResumePhase = run.Phase;
        run.Phase = GilVendorBuyPhase.Paused;
        run.Message = message;
        Persist();
        runtime.EndAutomation();
        return true;
    }

    public bool Resume(string currentContextSignature, out string error)
    {
        if (ActiveRun is not { Phase: GilVendorBuyPhase.Paused } run)
        {
            error = "No paused vendor buy run is available.";
            return false;
        }
        if (!string.Equals(run.ContextSignature, currentContextSignature, StringComparison.Ordinal))
        {
            error = "The current context does not match the frozen vendor buy plan.";
            return false;
        }
        run.Phase = run.ResumePhase == GilVendorBuyPhase.Paused
            ? GilVendorBuyPhase.RefreshPreconditions
            : run.ResumePhase;
        run.Message = "Vendor buy resumed.";
        runtime.BeginAutomation();
        Persist();
        error = string.Empty;
        return true;
    }

    public bool Stop(string message = "Vendor buy stopped.")
    {
        if (ActiveRun is not { } run || run.Phase is
            GilVendorBuyPhase.Completed or GilVendorBuyPhase.Stopped or GilVendorBuyPhase.Failed or GilVendorBuyPhase.Indeterminate)
            return false;
        if (run.ArmedPurchase is not null)
        {
            run.StopRequested = true;
            run.Phase = GilVendorBuyPhase.VerifyReceipt;
            run.Message = "Stop requested; reconciling the already-submitted purchase before stopping.";
            Persist();
            return true;
        }
        run.Phase = GilVendorBuyPhase.Stopped;
        run.Message = message;
        Persist();
        runtime.CloseShop();
        runtime.EndAutomation();
        return true;
    }

    private void TickRefreshPreconditions(GilVendorBuyRunSnapshot run)
    {
        var quantities = RemainingPurchaseQuantities(run);
        if (quantities.Count == 0)
        {
            Complete("Vendor buy completed.");
            return;
        }
        var snapshot = runtime.CaptureInventory(run.Lines.Select(line => line.ItemId).ToArray());
        if (!snapshot.IsComplete)
        {
            run.Message = snapshot.Message;
            return;
        }
        if (snapshot.Gil is null)
        {
            Pause("Player gil is temporarily unavailable; vendor buy will resume after it can be observed.");
            return;
        }
        var remainingGil = RemainingApprovedGil(run);
        if (snapshot.Gil.Value < remainingGil)
        {
            Fail(GilVendorBuyPhase.Failed, $"Remaining purchases require up to {remainingGil:N0} gil, but only {snapshot.Gil.Value:N0} gil is available.");
            return;
        }
        if (!runtime.HasCapacity(quantities, out var capacityError))
        {
            Pause(capacityError);
            return;
        }

        NormalizeCurrentStop(run);
        if (run.StopIndex >= run.Stops.Count)
        {
            Complete("Vendor buy completed.");
            return;
        }
        run.Phase = GilVendorBuyPhase.ReachVendor;
        run.Message = $"Traveling to {run.Stops[run.StopIndex].NpcName}.";
        runtime.ResetVendorApproach();
        Persist();
    }

    private void TickReachVendor(GilVendorBuyRunSnapshot run)
    {
        var stop = CurrentStop(run);
        var offer = run.Lines.First(line => stop.ItemIds.Contains(line.ItemId) && RemainingForLine(line) > 0).Offer!.ToOffer();
        var result = runtime.AdvanceToOpenShop(offer);
        run.Message = result.Message;
        switch (result.State)
        {
            case GilVendorReachState.Waiting:
                return;
            case GilVendorReachState.ShopOpen:
                run.Phase = GilVendorBuyPhase.ValidateShop;
                Persist();
                return;
            case GilVendorReachState.Unavailable:
                ReplanOrSkipCurrentStop(run, out var message);
                run.Phase = GilVendorBuyPhase.RefreshPreconditions;
                run.Message = message;
                runtime.ResetVendorApproach();
                Persist();
                return;
            default:
                Fail(GilVendorBuyPhase.Failed, DescribeReachFailure(run, stop.NpcName, result.Message));
                return;
        }
    }

    private void TickValidateShop(GilVendorBuyRunSnapshot run)
    {
        var stop = CurrentStop(run);
        var read = runtime.ReadShopRows();
        if (!read.IsSuccess)
        {
            Fail(GilVendorBuyPhase.Failed, read.Message);
            return;
        }
        var matches = new Dictionary<uint, int>();
        foreach (var itemId in stop.ItemIds)
        {
            var line = run.Lines.First(candidate => candidate.ItemId == itemId);
            if (RemainingForLine(line) <= 0)
                continue;
            var requestResult = GilVendorBuyRequest.Create(line.Offer!.ToOffer(), 1);
            if (!requestResult.IsSuccess)
            {
                Fail(GilVendorBuyPhase.Failed, requestResult.Message);
                return;
            }
            var match = GilVendorShopMatcher.FindMatchingRow(requestResult.Request!, read.Rows);
            if (!match.IsSuccess)
            {
                Fail(GilVendorBuyPhase.Failed, $"{line.ItemName}: {match.Message}");
                return;
            }
            matches[itemId] = match.Row!.RowIndex;
        }
        stop.MatchedShopRows = matches;
        stop.ShopValidated = true;
        run.LineIndex = 0;
        run.Phase = GilVendorBuyPhase.PurchaseLine;
        run.Message = $"Validated {matches.Count:N0} item line(s) at {stop.NpcName}.";
        Persist();
    }

    private void TickPurchaseLine(GilVendorBuyRunSnapshot run)
    {
        var stop = CurrentStop(run);
        while (run.LineIndex < stop.ItemIds.Count)
        {
            var line = run.Lines.First(candidate => candidate.ItemId == stop.ItemIds[run.LineIndex]);
            var snapshot = runtime.CaptureInventory([line.ItemId]);
            if (!snapshot.IsComplete || snapshot.Gil is null)
            {
                Pause(snapshot.Message);
                return;
            }
            var remaining = RemainingForLine(line);
            if (remaining <= 0)
            {
                line.Status = "Ceiling reached";
                run.LineIndex++;
                continue;
            }

            var batch = Math.Min(remaining, Math.Clamp(runtime.ResolveMaximumBatch(line.ItemId), 1, 99));
            var lineSpent = run.Receipts.Where(receipt => receipt.ItemId == line.ItemId)
                .Aggregate(0UL, (sum, receipt) => checked(sum + receipt.SpentGil));
            var totalSpent = run.Receipts.Aggregate(0UL, (sum, receipt) => checked(sum + receipt.SpentGil));
            var lineGilRemaining = line.ApprovedGilCeiling >= lineSpent ? line.ApprovedGilCeiling - lineSpent : 0;
            var totalGilRemaining = run.MaximumApprovedGil >= totalSpent ? run.MaximumApprovedGil - totalSpent : 0;
            var affordableWithinCeilings = line.UnitPriceGil == 0
                ? 0
                : checked((int)Math.Min(int.MaxValue, Math.Min(lineGilRemaining, totalGilRemaining) / line.UnitPriceGil));
            batch = Math.Min(batch, affordableWithinCeilings);
            if (batch <= 0)
            {
                line.Status = "Gil ceiling reached";
                run.LineIndex++;
                continue;
            }
            if (!runtime.HasCapacity(new Dictionary<uint, int> { [line.ItemId] = batch }, out var capacityError))
            {
                Pause(capacityError);
                return;
            }
            var request = GilVendorBuyRequest.Create(line.Offer!.ToOffer(), checked((uint)batch));
            if (!request.IsSuccess)
            {
                Fail(GilVendorBuyPhase.Failed, request.Message);
                return;
            }
            if (snapshot.Gil.Value < request.Request!.MaxTotalGil)
            {
                Fail(GilVendorBuyPhase.Failed, $"Not enough gil remains for the approved {line.ItemName} batch.");
                return;
            }

            run.ArmedPurchase = new GilVendorBuyArmedIntentSnapshot
            {
                ItemId = line.ItemId,
                Quantity = batch,
                ExpectedGil = request.Request.MaxTotalGil,
                ShopRowIndex = stop.MatchedShopRows[line.ItemId],
                BeforeItemCount = snapshot.ItemCounts.GetValueOrDefault(line.ItemId),
                BeforeGil = snapshot.Gil.Value,
                RetryCount = line.PurchaseRetryCount,
                ArmedAtUtc = utcNow().UtcDateTime,
            };
            line.Status = $"Buying {batch:N0}";
            run.Phase = GilVendorBuyPhase.VerifyReceipt;
            run.Message = $"Buying {batch:N0} {line.ItemName}.";
            Persist();

            try
            {
                if (!runtime.TrySubmitPurchase(
                        new GilVendorShopRow(run.ArmedPurchase.ShopRowIndex, line.ItemId, line.UnitPriceGil),
                        checked((uint)batch),
                        out var submitError))
                {
                    run.ArmedPurchase = null;
                    Fail(GilVendorBuyPhase.Failed, submitError);
                }
            }
            catch (Exception ex)
            {
                run.ArmedPurchase = null;
                Fail(GilVendorBuyPhase.Failed, $"Vendor purchase submission failed before a receipt could be observed: {ex.Message}");
            }
            return;
        }

        runtime.CloseShop();
        stop.ShopValidated = false;
        stop.MatchedShopRows.Clear();
        run.StopIndex++;
        run.LineIndex = 0;
        run.Phase = GilVendorBuyPhase.RefreshPreconditions;
        Persist();
    }

    private void TickVerifyReceipt(GilVendorBuyRunSnapshot run)
    {
        if (run.ArmedPurchase is not { } intent)
        {
            Fail(GilVendorBuyPhase.Indeterminate, "Purchase verification lost its persisted armed intent.");
            return;
        }
        runtime.TryConfirmPurchasePrompt();
        var line = run.Lines.First(candidate => candidate.ItemId == intent.ItemId);
        var snapshot = runtime.CaptureInventory([line.ItemId]);
        if (!snapshot.IsComplete || snapshot.Gil is null)
        {
            run.Message = snapshot.Message;
            return;
        }
        var request = GilVendorBuyRequest.Create(line.Offer!.ToOffer(), checked((uint)intent.Quantity)).Request!;
        var evidence = GilVendorPurchaseEvidenceClassifier.Classify(
            request,
            new(intent.BeforeItemCount, intent.BeforeGil),
            new(snapshot.ItemCounts.GetValueOrDefault(line.ItemId), snapshot.Gil.Value));
        if (evidence.Evidence == GilVendorPurchaseEvidence.Verified)
        {
            var receipt = evidence.Receipt!;
            run.Receipts.Add(new GilVendorBuyReceiptSnapshot
            {
                ItemId = receipt.ItemId,
                Quantity = checked((int)receipt.Quantity),
                SpentGil = receipt.SpentGil,
                BeforeItemCount = receipt.BeforeItemCount,
                AfterItemCount = receipt.AfterItemCount,
                BeforeGil = receipt.BeforeGil,
                AfterGil = receipt.AfterGil,
                VerifiedAtUtc = utcNow().UtcDateTime,
            });
            line.PurchasedQuantity = checked(line.PurchasedQuantity + (int)receipt.Quantity);
            line.PurchaseRetryCount = 0;
            line.Status = $"Verified {line.PurchasedQuantity:N0} bought";
            run.ArmedPurchase = null;
            if (run.StopRequested)
            {
                FinishStopped(run, "The already-submitted purchase was verified; vendor buy is now stopped.");
                return;
            }
            run.Phase = GilVendorBuyPhase.PurchaseLine;
            run.Message = $"Verified {receipt.Quantity:N0} {line.ItemName} for {receipt.SpentGil:N0} gil.";
            Persist();
            return;
        }
        if (evidence.Evidence == GilVendorPurchaseEvidence.Indeterminate)
        {
            Fail(GilVendorBuyPhase.Indeterminate, $"{line.ItemName}: {evidence.Message}");
            return;
        }
        if (utcNow().UtcDateTime - intent.ArmedAtUtc < ReceiptTimeout)
            return;
        if (run.StopRequested)
        {
            run.ArmedPurchase = null;
            FinishStopped(run, "No mutation was observed from the submitted purchase; vendor buy is now stopped.");
            return;
        }
        if (intent.RetryCount == 0)
        {
            line.PurchaseRetryCount = 1;
            run.ArmedPurchase = null;
            run.Phase = GilVendorBuyPhase.PurchaseLine;
            run.Message = $"No {line.ItemName} mutation was observed; retrying the unchanged batch once.";
            Persist();
            return;
        }
        Fail(GilVendorBuyPhase.Failed, $"No {line.ItemName} mutation was observed after the single safe retry.");
    }

    private void ReplanOrSkipCurrentStop(GilVendorBuyRunSnapshot run, out string message)
    {
        var failed = CurrentStop(run);
        var remaining = failed.ItemIds
            .Select(itemId => run.Lines.First(line => line.ItemId == itemId))
            .Where(line => RemainingForLine(line) > 0)
            .ToArray();
        var result = fallbackReplanner?.Invoke(new(
            CloneStop(failed),
            remaining.Select(line => new GilVendorBuyFallbackLine(
                line.ItemId,
                line.ItemName,
                RemainingForLine(line),
                CloneOffer(line.Offer!),
                line.AlternativeOffers.Select(CloneOffer).ToArray())).ToArray()));
        var selected = result?.Selections.ToDictionary(selection => selection.ItemId);
        var replacements = result?.ReplacementStops.Select(CloneStop).ToList() ?? [];
        foreach (var line in remaining)
        {
            if (selected is not null && selected.TryGetValue(line.ItemId, out var selection) &&
                line.AlternativeOffers.Any(offer => SameVendor(offer, selection.Offer)))
            {
                line.Offer = CloneOffer(selection.Offer);
                line.UnitPriceGil = selection.Offer.UnitPriceGil;
                line.AlternativeOffers.RemoveAll(offer => SameVendor(offer, selection.Offer));
            }
            else
            {
                line.VendorUnavailable = true;
                line.Status = "No accessible vendor";
                line.AlternativeOffers.Clear();
            }
        }
        replacements.RemoveAll(stop => stop.ItemIds.Any(itemId =>
            !remaining.Any(line => line.ItemId == itemId && !line.VendorUnavailable)));
        run.Stops.RemoveAt(run.StopIndex);
        run.Stops.InsertRange(run.StopIndex, replacements);
        run.LineIndex = 0;
        message = result?.Message ?? $"Skipped {string.Join(", ", remaining.Select(line => line.ItemName))} because no accessible vendor remains; continuing the vendor plan.";
    }

    private static Dictionary<uint, int> RemainingPurchaseQuantities(GilVendorBuyRunSnapshot run) =>
        run.Lines.Select(line => new { line.ItemId, Remaining = RemainingForLine(line) })
            .Where(line => line.Remaining > 0)
            .ToDictionary(line => line.ItemId, line => line.Remaining);

    private static ulong RemainingApprovedGil(GilVendorBuyRunSnapshot run) =>
        Math.Min(
            run.MaximumApprovedGil - Math.Min(run.MaximumApprovedGil, run.Receipts.Aggregate(0UL, (sum, receipt) => checked(sum + receipt.SpentGil))),
            run.Lines.Aggregate(0UL, (sum, line) => checked(sum + ((ulong)RemainingForLine(line) * line.UnitPriceGil))));

    private static int RemainingForLine(GilVendorBuyLineSnapshot line) =>
        line.VendorUnavailable ? 0 : Math.Max(0, line.ApprovedQuantity - line.PurchasedQuantity);

    private static void NormalizeCurrentStop(GilVendorBuyRunSnapshot run)
    {
        while (run.StopIndex < run.Stops.Count && run.Stops[run.StopIndex].ItemIds.All(itemId =>
                   RemainingForLine(run.Lines.First(line => line.ItemId == itemId)) <= 0))
            run.StopIndex++;
    }

    private static GilVendorBuyStopSnapshot CurrentStop(GilVendorBuyRunSnapshot run) => run.Stops[run.StopIndex];

    private static string DescribeReachFailure(GilVendorBuyRunSnapshot run, string npcName, string reason)
    {
        var spend = run.Receipts.Count == 0 ? "No gil was spent." : "Verified purchases from earlier stops were preserved.";
        return $"Couldn't reach {npcName}. {spend} {reason}".Trim();
    }

    private static bool SameVendor(GilVendorBuyOfferSnapshot left, GilVendorBuyOfferSnapshot right) =>
        left.NpcId == right.NpcId && left.ShopId == right.ShopId && left.TerritoryId == right.TerritoryId;

    private static GilVendorBuyLineSnapshot CloneLine(GilVendorBuyLineSnapshot line) => new()
    {
        ItemId = line.ItemId,
        ItemName = line.ItemName,
        ApprovedQuantity = line.ApprovedQuantity,
        PurchasedQuantity = line.PurchasedQuantity,
        PurchaseRetryCount = line.PurchaseRetryCount,
        UnitPriceGil = line.UnitPriceGil,
        ApprovedGilCeiling = line.ApprovedGilCeiling,
        VendorUnavailable = line.VendorUnavailable,
        Status = line.Status,
        Offer = line.Offer is null ? null : CloneOffer(line.Offer),
        AlternativeOffers = line.AlternativeOffers.Select(CloneOffer).ToList(),
    };

    private static GilVendorBuyStopSnapshot CloneStop(GilVendorBuyStopSnapshot stop) => new()
    {
        NpcId = stop.NpcId,
        ShopId = stop.ShopId,
        TerritoryId = stop.TerritoryId,
        NpcName = stop.NpcName,
        ItemIds = [.. stop.ItemIds],
        MatchedShopRows = new(stop.MatchedShopRows),
        ShopValidated = stop.ShopValidated,
    };

    private static GilVendorBuyOfferSnapshot CloneOffer(GilVendorBuyOfferSnapshot offer) =>
        GilVendorBuyOfferSnapshot.From(offer.ToOffer());

    private void Complete(string message)
    {
        if (ActiveRun is not { } run)
            return;
        run.Phase = GilVendorBuyPhase.Completed;
        run.Message = message;
        Persist();
        runtime.CloseShop();
        runtime.EndAutomation();
    }

    private void FinishStopped(GilVendorBuyRunSnapshot run, string message)
    {
        run.StopRequested = false;
        run.Phase = GilVendorBuyPhase.Stopped;
        run.Message = message;
        Persist();
        runtime.CloseShop();
        runtime.EndAutomation();
    }

    private void Fail(GilVendorBuyPhase phase, string message)
    {
        if (ActiveRun is not { } run)
            return;
        run.Phase = phase;
        run.Message = message;
        Persist();
        runtime.CloseShop();
        runtime.EndAutomation();
    }

    private void Persist()
    {
        if (ActiveRun is not { } run)
            return;
        run.UpdatedAtUtc = utcNow().UtcDateTime;
        store.Save(run);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        runtime.EndAutomation();
    }
}
