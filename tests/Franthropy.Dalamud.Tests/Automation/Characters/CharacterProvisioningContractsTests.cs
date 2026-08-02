using Franthropy.Dalamud.Automation.Characters;

namespace Franthropy.Dalamud.Tests.Automation.Characters;

public sealed class CharacterProvisioningContractsTests
{
    private static readonly CharacterCreationRecipe Recipe = new(
        ["Relay Example", "Relay Reserve"],
        "relay-default-v1");

    [Fact]
    public void Plan_RequiresWorldAtInvocationAndDefaultsToMarauder()
    {
        Assert.False(CharacterCreationPlan.TryCreate("Quaternary", null, Recipe, out _, out var missingWorld));
        Assert.Contains("world", missingWorld, StringComparison.OrdinalIgnoreCase);

        Assert.True(CharacterCreationPlan.TryCreate("Quaternary", "Siren", Recipe, out var plan, out var error), error);
        Assert.NotNull(plan);
        Assert.Equal("Siren", plan.World);
        Assert.Equal("Marauder", plan.StartingClass);
        Assert.Equal("F8806D1706B01E5A3DD90747C90E488B2F6B3580C61708797093F4B229709386", plan.Digest);
    }

    [Fact]
    public void Plan_DigestChangesWithThePerRunWorld()
    {
        Assert.True(CharacterCreationPlan.TryCreate("Quaternary", "Siren", Recipe, out var siren, out _));
        Assert.True(CharacterCreationPlan.TryCreate("Quaternary", "Faerie", Recipe, out var faerie, out _));

        Assert.NotEqual(siren!.Digest, faerie!.Digest);
    }

    [Fact]
    public void Plan_PreservesAValidExplicitClassOverride()
    {
        var recipe = Recipe with { StartingClass = "Arcanist" };

        Assert.True(CharacterCreationPlan.TryCreate("Quaternary", "Siren", recipe, out var plan, out var error), error);
        Assert.Equal("Arcanist", plan!.StartingClass);
    }

    [Fact]
    public void Commit_RequiresAnExactFreshRenderedReviewAndIsSingleUse()
    {
        var now = DateTimeOffset.Parse("2026-08-02T22:00:00Z");
        Assert.True(CharacterCreationPlan.TryCreate("Quaternary", "Siren", Recipe, out var plan, out _));
        var review = new CharacterCreationReview(
            "operation-1",
            plan!.Digest,
            plan.Lane,
            plan.World,
            plan.StartingClass,
            plan.NameCandidates[0],
            42,
            now);

        var issued = CharacterCreationCommitPolicy.Issue(plan, review, now);

        Assert.True(issued.Success, issued.Message);
        Assert.NotNull(issued.Receipt);
        Assert.True(CharacterCreationCommitPolicy.Validate(plan, review, issued.Receipt!, now.AddMinutes(1), false).Success);
        Assert.Equal("CommitAlreadyConsumed", CharacterCreationCommitPolicy.Validate(plan, review, issued.Receipt!, now.AddMinutes(1), true).Code);
        Assert.Equal("CommitExpired", CharacterCreationCommitPolicy.Validate(plan, review, issued.Receipt!, now.AddMinutes(6), false).Code);
    }

    [Fact]
    public void Commit_RejectsWorldOrRenderedClassDrift()
    {
        var now = DateTimeOffset.Parse("2026-08-02T22:00:00Z");
        Assert.True(CharacterCreationPlan.TryCreate("Quaternary", "Siren", Recipe, out var plan, out _));
        var wrongWorld = new CharacterCreationReview(
            "operation-1",
            plan!.Digest,
            plan.Lane,
            "Faerie",
            plan.StartingClass,
            plan.NameCandidates[0],
            42,
            now);

        var decision = CharacterCreationCommitPolicy.Issue(plan, wrongWorld, now);

        Assert.False(decision.Success);
        Assert.Equal("RenderedPlanMismatch", decision.Code);
    }

    [Fact]
    public void Commit_RejectsAStaleRenderedReview()
    {
        var now = DateTimeOffset.Parse("2026-08-02T22:00:00Z");
        Assert.True(CharacterCreationPlan.TryCreate("Quaternary", "Siren", Recipe, out var plan, out _));
        var review = new CharacterCreationReview(
            "operation-1",
            plan!.Digest,
            plan.Lane,
            plan.World,
            plan.StartingClass,
            plan.NameCandidates[0],
            42,
            now.AddMinutes(-1));

        var decision = CharacterCreationCommitPolicy.Issue(plan, review, now);

        Assert.False(decision.Success);
        Assert.Equal("StaleRenderedReview", decision.Code);
    }

    [Theory]
    [InlineData(CharacterCreationOutcomeObservation.Unknown, false, false, "CreationAmbiguous")]
    [InlineData(CharacterCreationOutcomeObservation.ExactCharacterPresent, true, false, "CharacterProven")]
    [InlineData(CharacterCreationOutcomeObservation.ExplicitlyRejected, false, true, "CreationRejected")]
    [InlineData(CharacterCreationOutcomeObservation.ExactCharacterAbsentAfterRefresh, false, true, "CharacterAbsent")]
    public void Reconciliation_NeverBlindlyRetriesAnUnknownOutcome(
        CharacterCreationOutcomeObservation observation,
        bool proven,
        bool mayIssueNewCommit,
        string code)
    {
        var decision = CharacterCreationReconciliationPolicy.Decide(observation);

        Assert.Equal(proven, decision.Proven);
        Assert.Equal(mayIssueNewCommit, decision.MayIssueNewCommit);
        Assert.Equal(code, decision.Code);
    }

    [Fact]
    public void StageDetector_RecognizesOneStageAndKeepsDialogsAsOverlays()
    {
        var observation = CharacterCreationStageDetector.Detect(
            ["_CharaMakeClassSelector", "SelectYesno"]);

        Assert.True(observation.Recognized);
        Assert.Equal(CharacterCreationStage.StartingClass, observation.Stage);
        Assert.Equal(["SelectYesno"], observation.VisibleOverlays);
    }

    [Fact]
    public void StageDetector_FailsClosedWhenTwoStagesAreVisible()
    {
        var observation = CharacterCreationStageDetector.Detect(
            ["_CharaMakeClassSelector", "_CharaMakeWorldServer"]);

        Assert.False(observation.Recognized);
        Assert.Equal(CharacterCreationStage.Unknown, observation.Stage);
        Assert.Equal("AmbiguousCharacterCreationStage", observation.Code);
    }

    [Fact]
    public void StageDetector_FailsClosedForAnUnreviewedGameVersion()
    {
        var observation = CharacterCreationStageDetector.Detect(
            ["_CharaMakeClassSelector"],
            observedGameVersion: "future-version",
            approvedGameVersion: CharacterProvisioningDefaults.ApprovedGameVersion);

        Assert.False(observation.Recognized);
        Assert.Equal("UnsupportedGameVersion", observation.Code);
    }
}
