using System.Security.Cryptography;
using System.Text;

namespace Franthropy.Dalamud.Automation.Characters;

public static class CharacterProvisioningDefaults
{
    public const int SchemaVersion = 1;
    public const string StartingClass = "Marauder";
    public const string ApprovedGameVersion = "2026.08.11.0000.0000";
    public static readonly TimeSpan MaximumReviewAge = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaximumCommitLifetime = TimeSpan.FromMinutes(10);
}

public enum CharacterCreationStage
{
    Unknown,
    Title,
    CharacterSelection,
    RaceAndGender,
    Tribe,
    Appearance,
    Nameday,
    Guardian,
    StartingClass,
    World,
    Name,
    FinalReview,
    Submitting,
    Loading,
    InWorld,
}

public sealed record CharacterCreationStageObservation(
    bool Recognized,
    CharacterCreationStage Stage,
    string Code,
    string Message,
    IReadOnlyList<string> VisibleStageAddons,
    IReadOnlyList<string> VisibleOverlays);

public static class CharacterCreationStageDetector
{
    private static readonly IReadOnlyDictionary<string, CharacterCreationStage> StageByAddon =
        new Dictionary<string, CharacterCreationStage>(StringComparer.Ordinal)
        {
            ["_TitleMenu"] = CharacterCreationStage.Title,
            ["_CharaSelectWorldServer"] = CharacterCreationStage.CharacterSelection,
            ["_CharaSelectListMenu"] = CharacterCreationStage.CharacterSelection,
            ["_CharaSelectReturn"] = CharacterCreationStage.CharacterSelection,
            ["_CharaMakeRaceGender"] = CharacterCreationStage.RaceAndGender,
            ["_CharaMakeTribe"] = CharacterCreationStage.Tribe,
            ["_CharaMakeFeature"] = CharacterCreationStage.Appearance,
            ["_CharaMakeBirthDay"] = CharacterCreationStage.Nameday,
            ["_CharaMakeGuardian"] = CharacterCreationStage.Guardian,
            ["_CharaMakeClassSelector"] = CharacterCreationStage.StartingClass,
            ["_CharaMakeWorldServer"] = CharacterCreationStage.World,
            ["_CharaMakeCharaName"] = CharacterCreationStage.Name,
            ["_CharaMakeNotice"] = CharacterCreationStage.FinalReview,
            ["_CharaMakeProgress"] = CharacterCreationStage.Submitting,
            ["NowLoading"] = CharacterCreationStage.Loading,
        };

    private static readonly HashSet<string> OverlayAddons = new(StringComparer.Ordinal)
    {
        "CharaMakeSelectYesNo",
        "_CharaMakeSelectYesNo",
        "SelectYesno",
        "SelectOk",
        "_TextError",
    };

    public static CharacterCreationStageObservation Detect(
        IEnumerable<string> visibleAddons,
        bool playerAvailable = false,
        string? observedGameVersion = null,
        string? approvedGameVersion = null)
    {
        ArgumentNullException.ThrowIfNull(visibleAddons);
        if (!string.IsNullOrWhiteSpace(approvedGameVersion) &&
            !string.Equals(observedGameVersion, approvedGameVersion, StringComparison.Ordinal))
            return new(false, CharacterCreationStage.Unknown, "UnsupportedGameVersion", $"Character provisioning has not been reviewed for game version '{observedGameVersion}'.", [], []);

        var visible = visibleAddons
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var overlays = visible.Where(OverlayAddons.Contains).Order(StringComparer.Ordinal).ToArray();
        if (playerAvailable)
            return new(true, CharacterCreationStage.InWorld, "InWorld", "An authenticated local player is available.", [], overlays);

        var stageAddons = visible.Where(StageByAddon.ContainsKey).Order(StringComparer.Ordinal).ToArray();
        var stages = stageAddons.Select(value => StageByAddon[value]).Distinct().ToArray();
        if (stages.Length == 0)
            return new(false, CharacterCreationStage.Unknown, "UnknownCharacterCreationStage", "No recognized character-provisioning stage is visible.", stageAddons, overlays);
        if (stages.Length > 1)
            return new(false, CharacterCreationStage.Unknown, "AmbiguousCharacterCreationStage", "More than one character-provisioning stage is visible.", stageAddons, overlays);

        return new(true, stages[0], "CharacterCreationStageRecognized", $"Recognized character-provisioning stage {stages[0]}.", stageAddons, overlays);
    }
}

public sealed record CharacterCreationRecipe(
    IReadOnlyList<string> NameCandidates,
    string AppearanceProfile,
    string? StartingClass = null);

public sealed record CharacterCreationPlan(
    int SchemaVersion,
    string Lane,
    string World,
    IReadOnlyList<string> NameCandidates,
    string AppearanceProfile,
    string StartingClass,
    string Digest)
{
    private static readonly HashSet<string> StartingClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Gladiator",
        "Pugilist",
        "Marauder",
        "Lancer",
        "Archer",
        "Conjurer",
        "Thaumaturge",
        "Arcanist",
    };

    public static bool TryCreate(
        string? lane,
        string? world,
        CharacterCreationRecipe? recipe,
        out CharacterCreationPlan? plan,
        out string error)
    {
        plan = null;
        var normalizedLane = lane?.Trim() ?? string.Empty;
        if (!IsBoundedText(normalizedLane, 1, 64))
        {
            error = "An exact launcher lane is required.";
            return false;
        }

        var normalizedWorld = world?.Trim() ?? string.Empty;
        if (!IsBoundedText(normalizedWorld, 3, 32))
        {
            error = "One world must be selected for this run.";
            return false;
        }

        if (recipe is null)
        {
            error = "A character recipe is required.";
            return false;
        }

        var appearance = recipe.AppearanceProfile?.Trim() ?? string.Empty;
        if (!IsBoundedText(appearance, 1, 64))
        {
            error = "A named appearance profile is required.";
            return false;
        }

        var startingClass = string.IsNullOrWhiteSpace(recipe.StartingClass)
            ? CharacterProvisioningDefaults.StartingClass
            : recipe.StartingClass.Trim();
        if (!StartingClasses.Contains(startingClass))
        {
            error = $"Unsupported starting class '{startingClass}'.";
            return false;
        }
        startingClass = StartingClasses.Single(value =>
            string.Equals(value, startingClass, StringComparison.OrdinalIgnoreCase));

        var names = (recipe.NameCandidates ?? [])
            .Select(value => value?.Trim() ?? string.Empty)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 0 || names.Any(value => !IsBoundedText(value, 3, 40)))
        {
            error = "At least one bounded, non-control character name candidate is required.";
            return false;
        }

        var digest = ComputeDigest(
            CharacterProvisioningDefaults.SchemaVersion,
            normalizedLane,
            normalizedWorld,
            names,
            appearance,
            startingClass);
        plan = new(
            CharacterProvisioningDefaults.SchemaVersion,
            normalizedLane,
            normalizedWorld,
            names,
            appearance,
            startingClass,
            digest);
        error = string.Empty;
        return true;
    }

    private static bool IsBoundedText(string value, int minimum, int maximum) =>
        value.Length >= minimum && value.Length <= maximum && !value.Any(char.IsControl);

    private static string ComputeDigest(
        int schemaVersion,
        string lane,
        string world,
        IReadOnlyList<string> names,
        string appearance,
        string startingClass)
    {
        var canonical = string.Join('\n',
            schemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            lane,
            world,
            appearance,
            startingClass,
            string.Join('\u001f', names));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

public sealed record CharacterCreationReview(
    string OperationId,
    string PlanDigest,
    string Lane,
    string World,
    string StartingClass,
    string NameCandidate,
    long FrameId,
    DateTimeOffset RenderedAtUtc);

public sealed record CharacterCreationCommitReceipt(
    string ReceiptId,
    string OperationId,
    string PlanDigest,
    string World,
    string StartingClass,
    string NameCandidate,
    long FrameId,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record CharacterCreationCommitDecision(
    bool Success,
    string Code,
    string Message,
    CharacterCreationCommitReceipt? Receipt = null);

public static class CharacterCreationCommitPolicy
{
    public static CharacterCreationCommitDecision Issue(
        CharacterCreationPlan plan,
        CharacterCreationReview review,
        DateTimeOffset now,
        TimeSpan? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(review);

        var match = ValidateReview(plan, review);
        if (!match.Success)
            return match;
        if (review.RenderedAtUtc > now || now - review.RenderedAtUtc > CharacterProvisioningDefaults.MaximumReviewAge)
            return Fail("StaleRenderedReview", "The rendered review is not current enough to authorize submission.");

        var requestedLifetime = lifetime ?? TimeSpan.FromMinutes(5);
        if (requestedLifetime <= TimeSpan.Zero || requestedLifetime > CharacterProvisioningDefaults.MaximumCommitLifetime)
            return Fail("InvalidCommitLifetime", "Commit authorization must expire within ten minutes.");

        return new(
            true,
            "CommitAuthorized",
            "The exact rendered character plan is authorized for one final submission.",
            new(
                Guid.NewGuid().ToString("N"),
                review.OperationId,
                plan.Digest,
                plan.World,
                plan.StartingClass,
                review.NameCandidate,
                review.FrameId,
                now,
                now.Add(requestedLifetime)));
    }

    public static CharacterCreationCommitDecision Validate(
        CharacterCreationPlan plan,
        CharacterCreationReview review,
        CharacterCreationCommitReceipt receipt,
        DateTimeOffset now,
        bool consumed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(receipt);

        var match = ValidateReview(plan, review);
        if (!match.Success)
            return match;
        if (consumed)
            return Fail("CommitAlreadyConsumed", "The commit receipt has already been used.");
        if (now > receipt.ExpiresAtUtc)
            return Fail("CommitExpired", "The commit receipt has expired; review the live final screen again.");
        if (string.IsNullOrWhiteSpace(receipt.ReceiptId) ||
            receipt.ExpiresAtUtc <= receipt.IssuedAtUtc ||
            receipt.ExpiresAtUtc - receipt.IssuedAtUtc > CharacterProvisioningDefaults.MaximumCommitLifetime ||
            receipt.IssuedAtUtc < review.RenderedAtUtc ||
            receipt.IssuedAtUtc > now)
            return Fail("InvalidCommitReceipt", "The commit receipt has invalid issuance or expiry bounds.");
        if (!string.Equals(receipt.OperationId, review.OperationId, StringComparison.Ordinal) ||
            !string.Equals(receipt.PlanDigest, plan.Digest, StringComparison.Ordinal) ||
            !string.Equals(receipt.World, plan.World, StringComparison.Ordinal) ||
            !string.Equals(receipt.StartingClass, plan.StartingClass, StringComparison.Ordinal) ||
            !string.Equals(receipt.NameCandidate, review.NameCandidate, StringComparison.Ordinal) ||
            receipt.FrameId != review.FrameId)
            return Fail("CommitReceiptMismatch", "The commit receipt does not match the current reviewed character plan.");

        return new(true, "CommitAccepted", "The single-use commit receipt matches the current reviewed character plan.", receipt);
    }

    private static CharacterCreationCommitDecision ValidateReview(CharacterCreationPlan plan, CharacterCreationReview review)
    {
        if (review.FrameId <= 0)
            return Fail("InvalidReviewFrame", "A positive rendered frame identifier is required.");
        if (!string.Equals(review.PlanDigest, plan.Digest, StringComparison.Ordinal) ||
            !string.Equals(review.Lane, plan.Lane, StringComparison.Ordinal) ||
            !string.Equals(review.World, plan.World, StringComparison.Ordinal) ||
            !string.Equals(review.StartingClass, plan.StartingClass, StringComparison.Ordinal) ||
            !plan.NameCandidates.Contains(review.NameCandidate, StringComparer.Ordinal))
            return Fail("RenderedPlanMismatch", "The live review does not match the requested character plan.");

        return new(true, "RenderedPlanMatched", "The live review matches the requested character plan.");
    }

    private static CharacterCreationCommitDecision Fail(string code, string message) => new(false, code, message);
}

public enum CharacterCreationOutcomeObservation
{
    Unknown,
    ExactCharacterPresent,
    ExplicitlyRejected,
    ExactCharacterAbsentAfterRefresh,
}

public sealed record CharacterCreationReconciliationDecision(
    bool Proven,
    bool MayIssueNewCommit,
    string Code,
    string Message);

public static class CharacterCreationReconciliationPolicy
{
    public static CharacterCreationReconciliationDecision Decide(CharacterCreationOutcomeObservation observation) => observation switch
    {
        CharacterCreationOutcomeObservation.ExactCharacterPresent =>
            new(true, false, "CharacterProven", "The exact name and world are present in the refreshed character list."),
        CharacterCreationOutcomeObservation.ExplicitlyRejected =>
            new(false, true, "CreationRejected", "The server explicitly rejected creation; a newly reviewed commit may be issued."),
        CharacterCreationOutcomeObservation.ExactCharacterAbsentAfterRefresh =>
            new(false, true, "CharacterAbsent", "A refreshed authoritative character list proves the exact character absent; a newly reviewed commit may be issued."),
        _ => new(false, false, "CreationAmbiguous", "Creation outcome is unknown; do not submit again until authoritative reconciliation succeeds."),
    };
}
