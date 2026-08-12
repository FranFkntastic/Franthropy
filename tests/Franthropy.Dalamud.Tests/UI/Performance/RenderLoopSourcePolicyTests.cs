using Franthropy.Dalamud.UI.Performance;

namespace Franthropy.Dalamud.Tests.UI.Performance;

public sealed class RenderLoopSourcePolicyTests
{
    [Fact]
    public void RejectsAnUnjustifiedRenderLoop()
    {
        const string source = """
            private void DrawRows()
            {
                foreach (var row in rows)
                    DrawRow(row);
            }
            """;

        var violation = Assert.Single(RenderLoopSourcePolicy.Analyze(source, "Bad.cs"));

        Assert.Equal("DrawRows", violation.MethodName);
        Assert.Equal("Bad.cs", violation.SourceName);
    }

    [Fact]
    public void AcceptsAConcreteBoundedJustification()
    {
        const string source = """
            [RenderFrameWorkJustification("Enum contains six static display modes.", 6)]
            private void DrawModes()
            {
                foreach (var mode in modes)
                    DrawMode(mode);
            }
            """;

        Assert.Empty(RenderLoopSourcePolicy.Analyze(source));
    }

    [Fact]
    public void IgnoresLoopsInsideCommentsAndStrings()
    {
        const string source = """
            private void DrawLabel()
            {
                // foreach (var row in rows) DrawRow(row);
                var text = "for (every frame)";
            }
            """;

        Assert.Empty(RenderLoopSourcePolicy.Analyze(source));
    }
}
