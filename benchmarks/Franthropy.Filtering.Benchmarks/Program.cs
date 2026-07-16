using System.Diagnostics;
using System.Runtime.InteropServices;
using Franthropy.Filtering.Compilation;
using Franthropy.Filtering.Evaluation;
using Franthropy.Filtering.Semantics;

var name = FilterFields.Text("item.name", "Item", "Item name.", ["name"]);
var quantity = FilterFields.Integer("instance.quantity", "Quantity", "Stack quantity.", ["quantity"], minimum: 0);
var quality = FilterFields.Text("instance.quality", "Quality", "NQ or HQ.", ["quality"]);
var catalog = new FilterCatalog([name, quantity, quality]);
var context = new FilterContextBuilder<Row>(catalog)
    .Bind(name, row => Evidence.Known(row.Name))
    .Bind(quantity, row => Evidence.Known(row.Quantity))
    .Bind(quality, row => Evidence.Known(row.Quality))
    .UseDefaultText(name)
    .Build("benchmark.item-instances", "1");
const string expression = "darksteel quality:HQ quantity>=20 NOT name:damaged";

for (var i = 0; i < 100; i++)
    _ = FilterCompiler.Compile(expression, context);

var compilationSamples = new double[2_000];
for (var i = 0; i < compilationSamples.Length; i++)
{
    var started = Stopwatch.GetTimestamp();
    _ = FilterCompiler.Compile(expression, context);
    compilationSamples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}

var records = Enumerable.Range(0, 50_000)
    .Select(index => new Row(index % 3 == 0 ? "Darksteel Ingot" : "Iron Ore", index % 99, index % 2 == 0 ? "HQ" : "NQ"))
    .ToArray();
var compilation = FilterCompiler.Compile(expression, context);
for (var i = 0; i < 5; i++)
    _ = CountMatches(records, compilation);

var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
var evaluationStarted = Stopwatch.GetTimestamp();
var matches = CountMatches(records, compilation);
var evaluationElapsed = Stopwatch.GetElapsedTime(evaluationStarted);
var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

Array.Sort(compilationSamples);
var p95 = compilationSamples[(int)Math.Floor(compilationSamples.Length * 0.95)];
Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"OS: {RuntimeInformation.OSDescription}");
Console.WriteLine($"Logical processors: {Environment.ProcessorCount}");
Console.WriteLine($"Compile p95: {p95:0.000} ms ({compilationSamples.Length:N0} samples)");
Console.WriteLine($"Evaluate 50,000: {evaluationElapsed.TotalMilliseconds:0.000} ms; {matches:N0} matches; {allocated:N0} B allocated");

return p95 < 5 && evaluationElapsed.TotalMilliseconds < 25 && allocated == 0 ? 0 : 1;

static int CountMatches(Row[] records, FilterCompilation<Row> compilation)
{
    var matches = 0;
    for (var i = 0; i < records.Length; i++)
    {
        if (compilation.Matches(records[i]))
            matches++;
    }
    return matches;
}

internal sealed record Row(string Name, long Quantity, string Quality);
