using System.Diagnostics;
using System.Runtime.InteropServices;
using Franthropy.Dalamud.Equipment;
using Franthropy.Dalamud.Performance;

var scenarios = new[]
{
    new Scenario("level-50-local", CandidatesPerPosition: 4, WorldCount: 1, BudgetMilliseconds: 750),
    new Scenario("level-100-data-center", CandidatesPerPosition: 8, WorldCount: 2, BudgetMilliseconds: 1_500),
    new Scenario("level-100-region", CandidatesPerPosition: 12, WorldCount: 3, BudgetMilliseconds: 3_000),
};

Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"OS: {RuntimeInformation.OSDescription}");
Console.WriteLine($"Logical processors: {Environment.ProcessorCount}");

var failed = false;
foreach (var scenario in scenarios)
{
    var request = EquipmentExactFrontierRepresentativeWorkload.Create(
        scenario.CandidatesPerPosition,
        scenario.WorldCount);
    _ = new EquipmentExactFrontierSolver().Solve(request);

    var samples = new double[3];
    EquipmentExactFrontierResult? result = null;
    for (var index = 0; index < samples.Length; index++)
    {
        var started = Stopwatch.GetTimestamp();
        result = new EquipmentExactFrontierSolver().Solve(request);
        samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    Array.Sort(samples);
    var median = samples[samples.Length / 2];
    var withinBudget = median < scenario.BudgetMilliseconds;
    failed |= !withinBudget;
    Console.WriteLine(
        $"{scenario.Name}: median {median:0.0} ms, range {samples[0]:0.0}-{samples[^1]:0.0} ms, " +
        $"budget {scenario.BudgetMilliseconds:N0} ms, peak {result!.Diagnostics.PeakRetainedStateCount:N0}, " +
        $"retained paths {result.Diagnostics.RetainedCompletePathCount:N0} [{(withinBudget ? "PASS" : "FAIL")}]");
}

return failed ? 1 : 0;

internal sealed record Scenario(string Name, int CandidatesPerPosition, int WorldCount, int BudgetMilliseconds);
