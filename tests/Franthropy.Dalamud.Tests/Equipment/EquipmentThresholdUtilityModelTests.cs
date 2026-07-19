using Franthropy.Dalamud.Equipment;

namespace Franthropy.Dalamud.Tests.Equipment;

public sealed class EquipmentThresholdUtilityModelTests
{
    [Fact]
    public void ThresholdStepOutweighsBoundedMonotonicProgress()
    {
        var model = CreateModel(Vector(99, 99), supported: true);

        var below = model.Evaluate(Vector(99, 200));
        var crossing = model.Evaluate(Vector(100, 0));

        Assert.True(crossing.UtilityScore > below.UtilityScore);
        Assert.Contains(crossing.Thresholds, threshold => threshold.ThresholdId == "a-100" && threshold.Satisfied);
        Assert.Contains(crossing.Contributions, contribution => contribution.Contribution == 1_000);
    }

    [Theory]
    [InlineData(100, 100, UpgradeAssessment.ClearImprovement)]
    [InlineData(99, 99, UpgradeAssessment.Equivalent)]
    [InlineData(98, 98, UpgradeAssessment.ClearRegression)]
    [InlineData(100, 98, UpgradeAssessment.ContextDependent)]
    public void AssessmentUsesComponentwiseComparison(int a, int b, UpgradeAssessment expected)
    {
        var model = CreateModel(Vector(99, 99), supported: true);

        var evaluation = model.Evaluate(Vector(a, b));

        Assert.Equal(expected, evaluation.Assessment);
    }

    [Fact]
    public void ResearchOnlyContextCannotGrantAuthority()
    {
        var model = CreateModel(Vector(99, 99), supported: false);

        var evaluation = model.Evaluate(Vector(100, 100));

        Assert.Equal(UpgradeAssessment.Unsupported, evaluation.Assessment);
        Assert.Equal(EquipmentEvaluationConfidence.Unknown, evaluation.Confidence);
        Assert.Contains(evaluation.Diagnostics, diagnostic => diagnostic.Contains("research-only", StringComparison.Ordinal));
    }

    [Fact]
    public void PartialDominanceIsGuaranteedOnlyByEveryDeclaredComponent()
    {
        var model = CreateModel(Vector(0, 0), supported: true);

        Assert.Equal(new(true, true), model.ComparePartial(Vector(2, 2), Vector(1, 1)));
        Assert.Equal(new(false, false), model.ComparePartial(Vector(2, 0), Vector(1, 1)));
    }

    [Fact]
    public void UndeclaredVectorComponentIsRejected()
    {
        var model = CreateModel(Vector(0, 0), supported: true);

        var exception = Assert.Throws<ArgumentException>(() => model.Evaluate(new([
            new("a", 1),
            new("unknown", 1),
        ])));

        Assert.Contains("unknown", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FixedComponentsApplyToCompletedLoadoutsWithoutChangingPartialDominance()
    {
        var definition = CreateDefinition(Vector(9, 0), supported: true) with
        {
            FixedComponents = Vector(90, 0),
        };
        var model = new EquipmentThresholdUtilityModel(definition);

        var evaluation = model.Evaluate(Vector(10, 0));

        Assert.Contains(evaluation.Thresholds, threshold => threshold.ThresholdId == "a-100" && threshold.Satisfied);
        Assert.Equal(100, evaluation.RawStats.Single(stat => stat.Semantic == EquipmentStatSemantic.Gathering).Value);
        Assert.Equal(new(true, true), model.ComparePartial(Vector(10, 0), Vector(9, 0)));
    }

    [Fact]
    public void ProvenOvershootSaturationCanonicalizesPartialDominanceButPreservesRenderedRawStats()
    {
        var model = CreateModel(Vector(100, 100), supported: true);

        var lowerOvershoot = model.Evaluate(Vector(150, 100));
        var higherOvershoot = model.Evaluate(Vector(250, 100));

        Assert.Equal(lowerOvershoot.UtilityScore, higherOvershoot.UtilityScore);
        Assert.Equal(UpgradeAssessment.Equivalent, lowerOvershoot.Assessment);
        Assert.Equal(UpgradeAssessment.Equivalent, higherOvershoot.Assessment);
        Assert.Equal(new(true, false), model.ComparePartial(Vector(250, 100), Vector(150, 100)));
        Assert.Equal(250, higherOvershoot.RawStats.Single(value => value.Semantic == EquipmentStatSemantic.Gathering).Value);
    }

    [Fact]
    public void FixedStatsReduceThePartialOvershootCeilingWithoutChangingTheTotalThreshold()
    {
        var definition = CreateDefinition(Vector(10, 0), supported: true) with
        {
            FixedComponents = Vector(90, 0),
        };
        var model = new EquipmentThresholdUtilityModel(definition);

        Assert.Equal(new(true, false), model.ComparePartial(Vector(50, 0), Vector(10, 0)));
        Assert.Equal(UpgradeAssessment.Equivalent, model.Evaluate(Vector(50, 0)).Assessment);
        Assert.Contains(model.Evaluate(Vector(10, 0)).Thresholds, value => value.ThresholdId == "a-100" && value.Satisfied);
    }

    [Fact]
    public void ExplicitScoreEnvelopeNormalizesComponentsThresholdsAndUncertaintyTogether()
    {
        var definition = CreateDefinition(Vector(0, 0), supported: true) with
        {
            RawScoreMaximum = 1_200,
            NormalizedScoreMaximum = 100,
        };
        var evaluation = new EquipmentThresholdUtilityModel(definition).Evaluate(Vector(100, 100));

        Assert.Equal(100d, evaluation.UtilityScore, 8);
        Assert.Equal(1_000d / 12d, evaluation.Contributions.Single(value => value.Weight == 0).Contribution, 8);
        Assert.Equal(10d / 12d, evaluation.Uncertainty.UpperBound - evaluation.UtilityScore, 8);
    }

    private static EquipmentThresholdUtilityModel CreateModel(
        EquipmentSolverUtilityVector baseline,
        bool supported) => new(CreateDefinition(baseline, supported));

    private static EquipmentThresholdUtilityModelDefinition CreateDefinition(
        EquipmentSolverUtilityVector baseline,
        bool supported)
    {
        var profile = new JobUtilityProfile(
            new("test", "1"),
            "Test",
            new HashSet<uint> { 1 },
            new HashSet<string> { "context" },
            [],
            "Synthetic test profile.");
        return new(
            profile,
            new("context", 1, 100, "test", []),
            baseline,
            [
                new("a", EquipmentStatSemantic.Gathering, 1, 100, "A progress"),
                new("b", EquipmentStatSemantic.Perception, 1, 100, "B progress"),
            ],
            [new("a-100", "A 100", [new("a", 100)], 1_000, "A capability")],
            10,
            ["Synthetic uncertainty."],
            supported);
    }

    private static EquipmentSolverUtilityVector Vector(long a, long b) => new([
        new("a", a),
        new("b", b),
    ]);
}
