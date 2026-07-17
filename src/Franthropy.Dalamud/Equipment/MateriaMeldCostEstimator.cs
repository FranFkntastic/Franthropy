namespace Franthropy.Dalamud.Equipment;

public sealed record MateriaMeldCostInput(
    string MeldId,
    ulong UnitPriceGil,
    double SuccessProbability);

public sealed record MateriaMeldCostLine(
    string MeldId,
    ulong UnitPriceGil,
    double SuccessProbability,
    double ExpectedCopies,
    int PlanningCopies);

/// <summary>
/// Expected spend and a conservative whole-plan stocking ceiling for independent materia melds.
/// The ceiling allocates confidence across every risky meld so all slots complete within the
/// displayed stock with at least the requested joint probability.
/// </summary>
public sealed record MateriaMeldCostEstimate(
    ulong OneCopyCostGil,
    ulong ExpectedCostGil,
    ulong PlanningCostGil,
    double PlanningConfidence,
    IReadOnlyList<MateriaMeldCostLine> Lines);

public static class MateriaMeldCostEstimator
{
    public static MateriaMeldCostEstimate Estimate(
        IReadOnlyList<MateriaMeldCostInput> melds,
        double planningConfidence = .90d)
    {
        ArgumentNullException.ThrowIfNull(melds);
        if (planningConfidence <= 0d || planningConfidence >= 1d || !double.IsFinite(planningConfidence))
            throw new ArgumentOutOfRangeException(nameof(planningConfidence), "Planning confidence must be finite and between zero and one.");

        var duplicate = melds
            .GroupBy(meld => meld.MeldId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Duplicate meld id '{duplicate.Key}'.", nameof(melds));
        foreach (var meld in melds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(meld.MeldId);
            if (meld.SuccessProbability <= 0d || meld.SuccessProbability > 1d || !double.IsFinite(meld.SuccessProbability))
                throw new ArgumentOutOfRangeException(nameof(melds), $"Meld '{meld.MeldId}' success probability must be finite and greater than zero through one.");
        }

        var riskyCount = melds.Count(meld => meld.SuccessProbability < 1d);
        var perMeldConfidence = riskyCount == 0
            ? planningConfidence
            : Math.Pow(planningConfidence, 1d / riskyCount);
        var lines = melds.Select(meld =>
        {
            var expectedCopies = 1d / meld.SuccessProbability;
            var planningCopies = meld.SuccessProbability >= 1d
                ? 1
                : checked((int)Math.Ceiling(
                    Math.Log(1d - perMeldConfidence) /
                    Math.Log(1d - meld.SuccessProbability)));
            return new MateriaMeldCostLine(
                meld.MeldId,
                meld.UnitPriceGil,
                meld.SuccessProbability,
                expectedCopies,
                Math.Max(1, planningCopies));
        }).ToArray();

        return new(
            Sum(lines, line => line.UnitPriceGil),
            CeilingSum(lines, line => line.UnitPriceGil * line.ExpectedCopies),
            Sum(lines, line => checked(line.UnitPriceGil * (ulong)line.PlanningCopies)),
            planningConfidence,
            lines);
    }

    private static ulong Sum<T>(IEnumerable<T> values, Func<T, ulong> selector) =>
        values.Aggregate(0UL, (total, value) => checked(total + selector(value)));

    private static ulong CeilingSum<T>(IEnumerable<T> values, Func<T, double> selector)
    {
        var total = values.Sum(selector);
        if (!double.IsFinite(total) || total > ulong.MaxValue)
            throw new OverflowException("Materia cost exceeds the supported gil range.");
        return checked((ulong)Math.Ceiling(total));
    }
}

/// <summary>
/// Current in-game DoH/DoL advanced-melding rates, expressed without UI or memory access.
/// Mirrored from Teamcraft's StaticData.dohdolMeldingRates on 2026-07-16; consumers remain
/// responsible for binding the table to an explicit patch envelope.
/// </summary>
public static class DohDolMateriaMeldingRates
{
    private static readonly int[][] HighQualityRates =
    [
        [90, 48, 28, 16],
        [82, 44, 26, 16],
        [70, 38, 22, 14],
        [58, 32, 20, 12],
        [17, 10, 7, 5],
        [17, 0, 0, 0],
        [17, 10, 7, 5],
        [17, 0, 0, 0],
        [17, 10, 7, 5],
        [17, 0, 0, 0],
        [17, 10, 7, 5],
        [17, 0, 0, 0],
    ];

    private static readonly int[][] NormalQualityRates =
    [
        [80, 40, 20, 10],
        [72, 36, 18, 10],
        [60, 30, 16, 8],
        [48, 24, 12, 6],
        [12, 6, 3, 2],
        [12, 0, 0, 0],
        [12, 6, 3, 2],
        [12, 0, 0, 0],
        [12, 6, 3, 2],
        [12, 0, 0, 0],
        [12, 6, 3, 2],
        [12, 0, 0, 0],
    ];

    public static double Resolve(bool highQuality, int materiaTier, int advancedMeldIndex)
    {
        if (materiaTier is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(materiaTier), "Materia tier must be from I through XII.");
        if (advancedMeldIndex is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(advancedMeldIndex), "Advanced meld index must be from zero through three.");
        var percent = (highQuality ? HighQualityRates : NormalQualityRates)[materiaTier - 1][advancedMeldIndex];
        if (percent == 0)
            throw new InvalidOperationException($"Tier {materiaTier} materia cannot be attached in advanced meld position {advancedMeldIndex + 1}.");
        return percent / 100d;
    }
}
