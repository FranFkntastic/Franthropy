using Franthropy.Dalamud.Equipment;

namespace Franthropy.Dalamud.UI.Plots;

public sealed record ParetoFrontierPlotModel(
    PlotSpec Spec,
    IReadOnlyDictionary<string, EquipmentDecisionSolution> SolutionsByDatumId);

/// <summary>
/// Product-neutral equipment Pareto specialization. Consumers retain control over surrounding
/// copy, nomination policy, inspector contents, and acquisition actions.
/// </summary>
public sealed class ParetoFrontierPlotBuilder
{
    public static PlotAttributeKey QualityMixAttribute { get; } = new("equipment.qualityMix");
    public static PlotAttributeKey PurchaseTransactionsAttribute { get; } = new("acquisition.purchaseTransactions");
    public static PlotAttributeKey ConfidenceAttribute { get; } = new("utility.confidence");
    public static PlotAttributeKey FrontierStatusAttribute { get; } = new("pareto.status");

    public ParetoFrontierPlotModel Build(EquipmentParetoResult result, string plotId = "equipment-pareto-frontier")
    {
        ArgumentNullException.ThrowIfNull(result);
        var all = result.Frontier
            .Concat(result.Dominated.Select(dominated => dominated.Solution))
            .GroupBy(solution => solution.Candidate.SolutionId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var byId = all.ToDictionary(solution => solution.Candidate.SolutionId, StringComparer.Ordinal);
        var maxTransactions = Math.Max(1, all.Select(solution => solution.Burden.PurchaseTransactions).DefaultIfEmpty(1).Max());
        var encodings = new PlotEncodingSet(
            new(
                QualityMixAttribute,
                [
                    new("NQ", new(.35f, .67f, .98f)),
                    new("HQ", new(.92f, .57f, .20f)),
                    new("Mixed", new(.67f, .45f, .94f)),
                ],
                new(.62f, .65f, .70f)),
            new(
                ConfidenceAttribute,
                [
                    new("High", PlotPointShape.Circle),
                    new("Medium", PlotPointShape.Diamond),
                    new("Low", PlotPointShape.Triangle),
                    new("Unknown", PlotPointShape.Square),
                ]),
            new(PurchaseTransactionsAttribute, new(0, maxTransactions), 4f, 9f),
            null,
            null);
        var frontierData = result.Frontier.Select(solution => Datum(solution, "Frontier")).ToArray();
        var dominatedData = result.Dominated.Select(value => Datum(value.Solution, "Dominated")).ToArray();
        var frontierLine = result.Frontier
            .OrderBy(solution => solution.AcquisitionCostGil)
            .ThenBy(solution => solution.Utility.UtilityScore)
            .Select(solution => Datum(solution, "Frontier"))
            .ToArray();
        var layers = new List<IPlotLayer>();
        if (dominatedData.Length > 0)
            layers.Add(new PlotPointLayer(
                "dominated-solutions",
                dominatedData,
                new(new(.46f, .49f, .54f), RadiusPixels: 4f, Opacity: .38f),
                encodings,
                15));
        if (frontierLine.Length > 1)
            layers.Add(new PlotPolylineLayer(
                "pareto-frontier-line",
                frontierLine,
                new(new(.78f, .81f, .87f, .72f), 1.5f),
                18));
        layers.Add(new PlotPointLayer(
            "pareto-solutions",
            frontierData,
            new(new(.35f, .67f, .98f), RadiusPixels: 5f),
            encodings,
            20));

        var xMaximum = Math.Max(1d, all.Select(solution => (double)solution.AcquisitionCostGil).DefaultIfEmpty(1).Max());
        var utilityMinimum = all.Select(solution => solution.Utility.UtilityScore).DefaultIfEmpty(0).Min();
        var utilityMaximum = all.Select(solution => solution.Utility.UtilityScore).DefaultIfEmpty(1).Max();
        var utilityPadding = Math.Max(1d, (utilityMaximum - utilityMinimum) * .08d);
        var spec = new PlotSpec(
            plotId,
            new(0, xMaximum * 1.05d),
            new(utilityMinimum - utilityPadding, utilityMaximum + utilityPadding),
            new("Acquisition cost", "gil", Format: FormatGil),
            new("Job utility"),
            layers,
            "Cost / utility frontier");
        return new(spec, byId);
    }

    private static PlotDatum Datum(EquipmentDecisionSolution solution, string status) => new(
        solution.Candidate.SolutionId,
        solution.AcquisitionCostGil,
        solution.Utility.UtilityScore,
        [
            new(QualityMixAttribute, new PlotCategoryAttribute(QualityMix(solution.Candidate))),
            new(PurchaseTransactionsAttribute, new PlotNumberAttribute(solution.Burden.PurchaseTransactions)),
            new(ConfidenceAttribute, new PlotCategoryAttribute(solution.Utility.Confidence.ToString())),
            new(FrontierStatusAttribute, new PlotCategoryAttribute(status)),
        ]);

    private static string QualityMix(EquipmentLoadoutCandidate candidate)
    {
        var qualities = candidate.Selections.Select(selection => selection.OfferKey.Quality).Distinct().ToArray();
        return qualities.Length switch
        {
            0 => "Unknown",
            1 when qualities[0] == EquipmentQuality.High => "HQ",
            1 => "NQ",
            _ => "Mixed",
        };
    }

    private static string FormatGil(double value) => value switch
    {
        >= 1_000_000 => $"{value / 1_000_000:0.#}m",
        >= 1_000 => $"{value / 1_000:0.#}k",
        _ => $"{value:0}",
    };
}
