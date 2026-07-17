using System.Numerics;
using Franthropy.Dalamud.Equipment;
using Franthropy.Dalamud.UI.Plots;

namespace Franthropy.Dalamud.Tests.UI.Plots;

public sealed class PlotSubstrateTests
{
    private static readonly PlotAttributeKey Quality = new("quality");
    private static readonly PlotAttributeKey Quantity = new("quantity");
    private static readonly PlotAttributeKey Confidence = new("confidence");

    [Fact]
    public void LinearScale_RoundTripsAndReversesYAxis()
    {
        var scale = new LinearPlotScale(new(0, 100), 500, 100);

        Assert.Equal(300f, scale.Map(50), 3);
        Assert.Equal(50, scale.Invert(300), 6);
    }

    [Fact]
    public void LinearScale_ExpandsDegenerateDomain()
    {
        var scale = new LinearPlotScale(new(42, 42), 0, 100);

        Assert.True(float.IsFinite(scale.Map(42)));
        Assert.Equal(50f, scale.Map(42), 3);
        Assert.True(scale.Domain.Length > 0);
    }

    [Fact]
    public void BrokenScale_CompressesOmittedDomainAndRoundTripsVisibleSegments()
    {
        var scale = new BrokenLinearPlotScale(new(0, 4_000_000), 0, 800, new(200_000, 2_600_000), 16);

        Assert.True(scale.Map(0) < scale.Map(200_000));
        Assert.True(scale.Map(200_000) < scale.Map(2_600_000));
        Assert.Equal(3_000_000, scale.Invert(scale.Map(3_000_000)), 3);
        Assert.False(scale.IsValueVisible(1_000_000));
        Assert.Equal(2, scale.VisibleDomainRanges.Count);
        Assert.Equal(2, scale.VisiblePixelRanges.Count);
    }

    [Fact]
    public void Ticks_CreateStableHumanScale()
    {
        Assert.Equal([0d, 20d, 40d, 60d, 80d, 100d], PlotTicks.Create(new(0, 100), 6));
        Assert.Equal([-5d, 0d, 5d], PlotTicks.Create(new(-6, 7), 4));
    }

    [Fact]
    public void Clipping_ClipsCrossingLineAndRejectsOutsideLine()
    {
        var rect = new PlotRect(Vector2.Zero, new(100, 100));

        Assert.True(PlotClipping.TryClipLine(rect, new(-20, 50), new(120, 50), out var start, out var end));
        Assert.Equal(new Vector2(0, 50), start);
        Assert.Equal(new Vector2(100, 50), end);
        Assert.False(PlotClipping.TryClipLine(rect, new(-20, -10), new(-5, -1), out _, out _));
    }

    [Fact]
    public void Encodings_AssignAttributesWithoutSelectionOverwritingThem()
    {
        var datum = Datum("point", 10, 20, "HQ", 4, "Low");
        var encodings = Encodings();

        var visual = PlotVisualResolver.Resolve(
            datum,
            new(new(.5f, .5f, .5f)),
            encodings,
            PlotPointRole.Selected | PlotPointRole.Warning);

        Assert.Equal(new PlotColor(.9f, .5f, .2f), visual.Color);
        Assert.Equal(PlotPointShape.Triangle, visual.Shape);
        Assert.Equal(9f, visual.RadiusPixels, 3);
        Assert.True(visual.Role.HasFlag(PlotPointRole.Selected));
        Assert.True(visual.Role.HasFlag(PlotPointRole.Warning));
    }

    [Fact]
    public void Compiler_ProducesAllLayerPrimitivesAndSemanticHitTargets()
    {
        var data = new[]
        {
            Datum("a", 10, 20, "NQ", 1, "High"),
            Datum("b", 80, 70, "HQ", 4, "Low"),
        };
        IPlotLayer[] layers =
        [
            new PlotBandLayer("band", PlotRuleOrientation.Vertical, 20, 40, new(new(.2f, .3f, .4f, .2f))),
            new PlotRuleLayer("rule", PlotRuleOrientation.Horizontal, 50, new(new(.8f, .2f, .2f))),
            new PlotPolylineLayer("line", data, new(new(.8f, .8f, .8f))),
            new PlotStepLayer("step", data, new(new(.5f, .8f, .5f))),
            new PlotPointLayer("points", data, new(new(.5f, .5f, .5f)), Encodings()),
            new PlotAnnotationLayer("note", 50, 50, "threshold", new(1, 1, 1), new(3, 3)),
        ];
        var spec = new PlotSpec("test", new(0, 100), new(0, 100), new("Cost"), new("Utility"), layers);

        var frame = new PlotCompiler().Compile(spec, new(Vector2.Zero, new(600, 400)));

        Assert.Equal(2, frame.HitTargets.Count);
        Assert.Contains(frame.Commands, command => command is PlotRectCommand);
        Assert.Contains(frame.Commands, command => command is PlotPointCommand { DatumId: "a" });
        Assert.Contains(frame.Commands, command => command is PlotLineCommand);
        Assert.Contains(frame.Commands, command => command is PlotTextCommand { Text: "threshold" });
        var nearest = PlotHitTesting.FindNearest(frame.HitTargets, frame.HitTargets[0].Position);
        Assert.Equal("a", nearest?.DatumId);
        Assert.Equal("b", PlotHitTesting.Traverse(frame.HitTargets, "a", 1));
        Assert.Equal("a", PlotHitTesting.Traverse(frame.HitTargets, "b", 1));
    }

    [Fact]
    public void Compiler_DynamicallyBreaksLargeLeadingEmptyRange()
    {
        var data = new[]
        {
            Datum("first", 2_700_000, 40, "HQ", 1, "High"),
            Datum("last", 4_000_000, 80, "HQ", 1, "High"),
        };
        var spec = new PlotSpec(
            "broken",
            new(0, 4_100_000),
            new(0, 100),
            new("Cost", "gil"),
            new("Utility"),
            [new PlotPointLayer("points", data, new(new(.5f, .5f, .5f)), Encodings())],
            XAxisBreak: new());

        var frame = new PlotCompiler().Compile(spec, new(Vector2.Zero, new(900, 400)));
        var scale = Assert.IsType<BrokenLinearPlotScale>(frame.XScale);
        var plottedSpan = scale.Map(4_000_000) - scale.Map(2_700_000);

        Assert.True(plottedSpan > frame.Layout.DataArea.Width * .65f);
        Assert.DoesNotContain(frame.Commands.OfType<PlotTextCommand>(), command => command.Text == "1000000");
        Assert.Equal(2, frame.HitTargets.Count);
    }

    [Fact]
    public void Viewport_ZoomsPansClampsAndReturnsToFit()
    {
        var spec = new PlotSpec("viewport", new(0, 100), new(0, 200), new("X"), new("Y"), []);
        var viewport = new PlotViewportState();

        viewport.Zoom(spec, 50, 100, .5d);
        var zoomed = viewport.Apply(spec);
        Assert.Equal(new PlotRange(25, 75), zoomed.XDomain);
        Assert.Equal(new PlotRange(50, 150), zoomed.YDomain);
        Assert.False(viewport.IsFit);

        viewport.Pan(spec, 100, -100);
        var clamped = viewport.Apply(spec);
        Assert.Equal(new PlotRange(50, 100), clamped.XDomain);
        Assert.Equal(new PlotRange(0, 100), clamped.YDomain);

        viewport.Fit();
        Assert.True(viewport.IsFit);
        Assert.Equal(spec, viewport.Apply(spec));
    }

    [Fact]
    public void ParetoSpecialization_MapsQualityTransactionsAndConfidenceToPointAttributes()
    {
        var frontier = Solution("frontier", 10_000, 80, EquipmentQuality.High, 3, EquipmentEvaluationConfidence.Low) with
        {
            AcquisitionCostEstimate = new(8_000, 10_000, 25_000, .90d, ["Overmeld failures"]),
        };
        var dominated = Solution("dominated", 12_000, 70, EquipmentQuality.Normal, 1, EquipmentEvaluationConfidence.High);
        var result = new EquipmentParetoResult(
            [frontier],
            [new(dominated, ["frontier"])],
            [],
            []);

        var model = new ParetoFrontierPlotBuilder().Build(result);
        var frontierLayer = Assert.IsType<PlotPointLayer>(model.Spec.Layers.Single(layer => layer.Id == "pareto-solutions"));
        var datum = Assert.Single(frontierLayer.Data);
        var visual = PlotVisualResolver.Resolve(datum, frontierLayer.Style, frontierLayer.Encodings, PlotPointRole.None);

        Assert.Equal("HQ", Assert.IsType<PlotCategoryAttribute>(datum.GetAttribute(ParetoFrontierPlotBuilder.QualityMixAttribute)).Value);
        Assert.Equal(3, Assert.IsType<PlotNumberAttribute>(datum.GetAttribute(ParetoFrontierPlotBuilder.PurchaseTransactionsAttribute)).Value);
        Assert.Equal(25_000, Assert.IsType<PlotNumberAttribute>(datum.GetAttribute(ParetoFrontierPlotBuilder.PlanningCostAttribute)).Value);
        Assert.Equal(.90d, Assert.IsType<PlotNumberAttribute>(datum.GetAttribute(ParetoFrontierPlotBuilder.PlanningConfidenceAttribute)).Value);
        Assert.Equal(new PlotColor(.92f, .57f, .20f), visual.Color);
        Assert.Equal(PlotPointShape.Triangle, visual.Shape);
        Assert.True(visual.RadiusPixels > 4);
        Assert.Contains(model.Spec.Layers, layer => layer.Id == "dominated-solutions");
        Assert.NotNull(model.Spec.XAxisBreak);
    }

    [Fact]
    public void DatumReplay_PreservesEveryTypedAttributeIndependentOfEncodings()
    {
        var datum = new PlotDatum(
            "semantic-id",
            12,
            34,
            [
                new(new("z-text"), new PlotTextAttribute("label")),
                new(new("a-number"), new PlotNumberAttribute(12.5)),
                new(new("m-category"), new PlotCategoryAttribute("HQ")),
                new(new("b-boolean"), new PlotBooleanAttribute(true)),
            ]);

        var json = PlotDatumReplayJson.Serialize(new([datum]));
        var roundTrip = Assert.Single(PlotDatumReplayJson.Deserialize(json).Data);

        Assert.Equal("semantic-id", roundTrip.Id);
        Assert.Equal(12.5, Assert.IsType<PlotNumberAttribute>(roundTrip.GetAttribute(new("a-number"))).Value);
        Assert.True(Assert.IsType<PlotBooleanAttribute>(roundTrip.GetAttribute(new("b-boolean"))).Value);
        Assert.Equal("HQ", Assert.IsType<PlotCategoryAttribute>(roundTrip.GetAttribute(new("m-category"))).Value);
        Assert.Equal("label", Assert.IsType<PlotTextAttribute>(roundTrip.GetAttribute(new("z-text"))).Value);
        Assert.True(json.IndexOf("a-number", StringComparison.Ordinal) < json.IndexOf("z-text", StringComparison.Ordinal));
    }

    [Fact]
    public void Overlay_UnionsDomainsAndNamespacesSemanticIdentity()
    {
        var first = new PlotSpec(
            "first",
            new(0, 100),
            new(10, 30),
            new("Cost", "gil"),
            new("Utility"),
            [new PlotPointLayer("points", [Datum("same", 25, 20, "HQ", 1, "High")], new(new(.5f, .5f, .5f)), Encodings())]);
        var second = new PlotSpec(
            "second",
            new(0, 250),
            new(5, 50),
            new("Cost", "gil"),
            new("Utility"),
            [new PlotPointLayer("points", [Datum("same", 200, 45, "NQ", 2, "Low")], new(new(.5f, .5f, .5f)), Encodings())]);

        var overlay = PlotOverlayComposer.Compose("overlay",
        [
            new("ordinary", first, new(PointShape: PlotPointShape.Circle)),
            new("legendary", second, new(PointShape: PlotPointShape.Diamond)),
        ]);

        Assert.Equal(new PlotRange(0, 250), overlay.Spec.XDomain);
        Assert.Equal(new PlotRange(5, 50), overlay.Spec.YDomain);
        Assert.Equal(2, overlay.DatumIdentities.Count);
        Assert.Equal(new("ordinary", "same"), overlay.DatumIdentities["ordinary/same"]);
        Assert.Equal(new("legendary", "same"), overlay.DatumIdentities["legendary/same"]);
        var ordinary = Assert.IsType<PlotPointLayer>(overlay.Spec.Layers.Single(layer => layer.Id == "ordinary/points"));
        var legendary = Assert.IsType<PlotPointLayer>(overlay.Spec.Layers.Single(layer => layer.Id == "legendary/points"));
        Assert.Equal(PlotPointShape.Circle, ordinary.Style.Shape);
        Assert.Equal(PlotPointShape.Diamond, legendary.Style.Shape);
        Assert.Null(ordinary.Encodings.Shape);
        Assert.Equal("ordinary", Assert.IsType<PlotCategoryAttribute>(ordinary.Data[0].GetAttribute(PlotOverlayComposer.SeriesAttribute)).Value);
        Assert.Equal(2, new PlotCompiler().Compile(overlay.Spec, new(Vector2.Zero, new(600, 400))).HitTargets.Count);
    }

    [Fact]
    public void Overlay_RejectsPlotsWithoutSharedAxes()
    {
        PlotSpec Spec(string yLabel) => new(
            yLabel,
            new(0, 1),
            new(0, 1),
            new("Cost", "gil"),
            new(yLabel),
            []);

        var exception = Assert.Throws<ArgumentException>(() => PlotOverlayComposer.Compose(
            "overlay",
            [new("ordinary", Spec("Utility")), new("depth", Spec("Listings"))]));

        Assert.Contains("does not share the same X and Y axes", exception.Message);
    }

    private static PlotEncodingSet Encodings() => new(
        new(Quality, [new("NQ", new(.2f, .6f, .9f)), new("HQ", new(.9f, .5f, .2f))], new(.5f, .5f, .5f)),
        new(Confidence, [new("High", PlotPointShape.Circle), new("Low", PlotPointShape.Triangle)]),
        new(Quantity, new(1, 4), 4, 9));

    private static PlotDatum Datum(string id, double x, double y, string quality, double quantity, string confidence) => new(
        id,
        x,
        y,
        [
            new(Quality, new PlotCategoryAttribute(quality)),
            new(Quantity, new PlotNumberAttribute(quantity)),
            new(Confidence, new PlotCategoryAttribute(confidence)),
        ]);

    private static EquipmentDecisionSolution Solution(
        string id,
        ulong cost,
        double utility,
        EquipmentQuality quality,
        int purchases,
        EquipmentEvaluationConfidence confidence)
    {
        var candidate = new EquipmentLoadoutCandidate(id,
        [
            new(EquipmentLoadoutPosition.Head, new(100, quality, EquipmentAcquisitionSourceKind.MarketBoard, id)),
        ]);
        var evaluation = new EquipmentUtilityEvaluation(
            new("profile", "1"),
            new("context", 19, 50, "test", []),
            utility,
            new(utility, utility, []),
            UpgradeAssessment.ClearImprovement,
            [],
            [],
            [],
            confidence,
            []);
        return new(candidate, evaluation, cost, new(0, 0, purchases), new(0, 0, 0), []);
    }
}
