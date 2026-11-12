using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.UI.Tables;

namespace Franthropy.Dalamud.Tests.UI.Tables;

[CollectionDefinition("Native table renderer", DisableParallelization = true)]
public sealed class NativeTableRendererCollection;

[Collection("Native table renderer")]
public sealed class DalamudTableNativeTests
{
    [Fact]
    public unsafe void Unbalanced_nested_throw_does_not_pop_the_nested_tables_channel()
    {
        var table = new DalamudTableProjection<int>([new("A", 100, _ => "A")]);
        Frames(2, _ =>
        {
            Assert.True(table.Begin("unbalanced", DalamudTableLayout.FitContent(DalamudTableLayout.DefaultFlags)));
            try
            {
                table.DrawSaneRow([1], 0, "summary");
                var outer = ImGuiP.GetCurrentTable().ID;
                Assert.Throws<InvalidOperationException>(() => table.DrawDetailRow("detail", () =>
                {
                    Assert.True(ImGui.BeginTable("inner", 2));
                    throw new InvalidOperationException("Unbalanced consumer");
                }));
                Assert.NotEqual(outer, ImGuiP.GetCurrentTable().ID);
                Assert.Equal(-1, ImGuiP.GetCurrentTable().CurrentColumn);
                // The host recovers callback-owned scopes first. Only then is it
                // safe to unwind the untouched helper scopes on their own table.
                ImGui.EndTable();
                ImGui.PopClipRect();
                ImGui.PopID();
                ImGuiP.TablePopBackgroundChannel();
            }
            finally { table.End(); }
        });
    }

    [Fact]
    public unsafe void Throwing_balanced_nested_callback_restores_the_owning_scope()
    {
        var table = new DalamudTableProjection<int>([new("A", 100, _ => "A"), new("B", 100, _ => "B")]);
        Frames(2, _ =>
        {
            Assert.True(table.Begin("exception", DalamudTableLayout.FitContent(DalamudTableLayout.DefaultFlags)));
            try
            {
                table.DrawSaneRow([1], 0, "summary");
                var id = ImGuiP.GetCurrentTable().ID;
                var ids = ImGuiP.GetCurrentWindow().IDStack.Size;
                Assert.Throws<InvalidOperationException>(() => table.DrawDetailRow("detail", () =>
                {
                    if (!ImGui.BeginTable("inner", 2)) throw new InvalidOperationException("Hidden inner table");
                    try { throw new InvalidOperationException("Content failure"); }
                    finally { ImGui.EndTable(); }
                }));
                Assert.Equal(id, ImGuiP.GetCurrentTable().ID);
                Assert.Equal(ids, ImGuiP.GetCurrentWindow().IDStack.Size);
                table.DrawSaneRow([2], 0, "following");
            }
            finally { table.End(); }
        });
    }

    private static unsafe void Frames(int count, Action<int> draw, Action<int>? input = null)
    {
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO();
            io.IniFilename = null;
            io.LogFilename = null;
            io.DisplaySize = new(800, 600);
            io.DeltaTime = 1f / 60;
            io.ConfigInputTrickleEventQueue = false;
            io.Fonts.AddFontDefault();
            Assert.True(io.Fonts.Build());
            for (var frame = 0; frame < count; frame++)
            {
                input?.Invoke(frame);
                ImGui.NewFrame();
                ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
                ImGui.SetNextWindowSize(new(700, 500), ImGuiCond.Always);
                ImGui.Begin("Native fixture", ImGuiWindowFlags.NoSavedSettings);
                try { draw(frame); }
                finally { ImGui.End(); }
                ImGui.Render();
            }
        }
        finally { ImGui.DestroyContext(context); }
    }
}
