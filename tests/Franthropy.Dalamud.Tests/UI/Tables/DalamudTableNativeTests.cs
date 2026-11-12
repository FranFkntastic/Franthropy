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
    public void Hidden_first_column_does_not_steal_a_secondary_button_click()
    {
        var clicked = false;
        var selected = new TableSelectionModel<int>();
        var target = Vector2.Zero;
        var table = new DalamudTableProjection<int>([
            new("Item", 100, _ => "Hidden", Flags: ImGuiTableColumnFlags.DefaultHide),
            new("Rule", 100, _ => "", Draw: _ =>
            {
                clicked |= ImGui.Button("On/Off");
                target = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) / 2;
            }),
        ], DalamudTableSelection<int>.Multi(selected, n => n));
        Frames(4, frame =>
        {
            Assert.True(table.Begin("hidden", DalamudTableLayout.FitContent(DalamudTableLayout.DefaultFlags)));
            try { table.DrawSaneRow([1], 0, "row"); }
            finally { table.End(); }
        }, frame => MouseClick(frame, target));
        Assert.True(clicked);
        Assert.False(selected.IsSelected(1));
    }

    [Fact]
    public void Row_activates_on_mouse_down_without_toggling_again_on_release()
    {
        var activations = 0;
        var target = Vector2.Zero;
        var table = new DalamudTableProjection<int>([new("Item", 100, _ => "Row")],
            DalamudTableSelection<int>.Single(_ => false, _ => activations++));
        Frames(4, _ =>
        {
            Assert.True(table.Begin("activation", DalamudTableLayout.FitContent(DalamudTableLayout.DefaultFlags)));
            try
            {
                table.DrawSaneRow([1], 0, "row");
                target = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) / 2;
            }
            finally { table.End(); }
        }, frame => MouseClick(frame, target));
        Assert.Equal(1, activations);
    }

    [Fact]
    public void Row_honors_native_keyboard_activation()
    {
        var activations = 0;
        var table = new DalamudTableProjection<int>([new("Item", 100, _ => "Row")],
            DalamudTableSelection<int>.Single(_ => false, _ => activations++));
        Frames(5, frame =>
        {
            Assert.True(table.Begin("keyboard", DalamudTableLayout.FitContent(DalamudTableLayout.DefaultFlags)));
            try
            {
                if (frame == 1) ImGui.SetKeyboardFocusHere();
                table.DrawSaneRow([1], 0, "row");
            }
            finally { table.End(); }
        }, frame =>
        {
            ImGui.GetIO().ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
            if (frame == 3) ImGui.GetIO().AddKeyEvent(ImGuiKey.Space, true);
            if (frame == 4) ImGui.GetIO().AddKeyEvent(ImGuiKey.Space, false);
        });
        Assert.Equal(1, activations);
    }

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
    public unsafe void Detail_spans_columns_and_advances_the_following_row()
    {
        var table = new DalamudTableProjection<int>([
            new("A", 100, _ => "A"), new("B", 100, _ => "B"), new("C", 100, _ => "C"),
        ]);
        Frames(2, _ =>
        {
            Assert.True(table.Begin("layout", DalamudTableLayout.FitContent(DalamudTableLayout.DefaultFlags)));
            try
            {
                table.DrawSaneRow([1], 0, "summary");
                var tableId = ImGuiP.GetCurrentTable().ID;
                var detailBottom = 0f;
                table.DrawDetailRow("detail", () =>
                {
                    Assert.True(ImGui.GetContentRegionAvail().X > 250);
                    ImGui.Dummy(new(ImGui.GetContentRegionAvail().X, 90));
                    detailBottom = ImGui.GetItemRectMax().Y;
                });
                Assert.Equal(tableId, ImGuiP.GetCurrentTable().ID);
                table.DrawSaneRow([2], 0, "following");
                Assert.True(ImGui.GetItemRectMin().Y >= detailBottom);
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

    private static void MouseClick(int frame, Vector2 target)
    {
        if (frame == 2)
        {
            ImGui.GetIO().AddMousePosEvent(target.X, target.Y);
            ImGui.GetIO().AddMouseButtonEvent(0, true);
        }
        if (frame == 3) ImGui.GetIO().AddMouseButtonEvent(0, false);
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
