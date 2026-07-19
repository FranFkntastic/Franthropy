using Franthropy.Dalamud.AgentBridge;

namespace Franthropy.Dalamud.Tests.AgentBridge;

public sealed class RenderedUiVirtualPointerPolicyTests
{
    [Theory]
    [InlineData(1394, false, 0, 1394)]
    [InlineData(1394, true, 0.8, 1742)]
    [InlineData(314, true, 0.8, 392)]
    public void Rendered_coordinates_are_converted_to_the_collision_input_space(
        float rendered,
        bool scaled,
        float scale,
        int expected)
    {
        Assert.True(RenderedUiVirtualPointerPolicy.TryConvertRenderedCoordinate(rendered, scaled, scale, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(float.NaN)]
    public void Scaled_coordinate_conversion_rejects_invalid_layout(float scale)
    {
        Assert.False(RenderedUiVirtualPointerPolicy.TryConvertRenderedCoordinate(100, true, scale, out _));
    }

    [Theory]
    [InlineData(10, 10, 20, 20, 15, 15, true)]
    [InlineData(10, 10, 20, 20, 20, 15, false)]
    [InlineData(10, 10, 10, 20, 10, 15, false)]
    public void Collision_geometry_requires_a_point_inside_a_nonempty_rendered_box(
        float left,
        float top,
        float right,
        float bottom,
        float x,
        float y,
        bool expected)
    {
        Assert.Equal(expected, RenderedUiVirtualPointerPolicy.ContainsPoint(left, top, right, bottom, x, y));
    }

    [Fact]
    public void Move_preserves_non_action_state_but_clears_every_click_button_and_wheel_field()
    {
        var source = State();
        var moved = RenderedUiVirtualPointerPolicy.CreateMove(source, 1400, 320);

        Assert.Equal(1400, moved.PositionX);
        Assert.Equal(320, moved.PositionY);
        Assert.Equal(0, moved.MouseWheel);
        Assert.Equal(0, moved.MouseButtonHeldFlags);
        Assert.Equal(0, moved.MouseButtonPressedFlags);
        Assert.Equal(0, moved.MouseButtonReleasedFlags);
        Assert.Equal(0, moved.MouseButtonHeldThrottledFlags);
        Assert.Equal(1300, moved.DeltaX);
        Assert.Equal(120, moved.DeltaY);
        Assert.True(moved.IsGameWindowFocused);
    }

    [Fact]
    public void Execute_restores_the_exact_snapshot_after_success()
    {
        var snapshot = State();
        var current = snapshot;
        var temporary = RenderedUiVirtualPointerPolicy.CreateMove(snapshot, 1400, 320);

        var result = RenderedUiVirtualPointerPolicy.ExecuteRestored(
            snapshot,
            temporary,
            state => current = state,
            () =>
            {
                Assert.Equal(temporary, current);
                return 42;
            });

        Assert.Equal(42, result);
        Assert.Equal(snapshot, current);
    }

    [Fact]
    public void Execute_restores_the_exact_snapshot_when_cancelled()
    {
        var snapshot = State();
        var current = snapshot;
        var temporary = RenderedUiVirtualPointerPolicy.CreateMove(snapshot, 1400, 320);

        Assert.Throws<OperationCanceledException>(() =>
            RenderedUiVirtualPointerPolicy.ExecuteRestored<bool>(
                snapshot,
                temporary,
                state => current = state,
                () => throw new OperationCanceledException()));

        Assert.Equal(snapshot, current);
    }

    [Fact]
    public void Pointer_scope_restores_the_exact_source_after_cancellation()
    {
        nint current = 17;

        Assert.Throws<OperationCanceledException>(() =>
            RenderedUiVirtualPointerPolicy.ExecutePointerRestored<bool>(
                snapshot: 17,
                temporary: 23,
                apply: pointer => current = pointer,
                action: () =>
                {
                    Assert.Equal((nint)23, current);
                    throw new OperationCanceledException();
                }));

        Assert.Equal((nint)17, current);
    }

    [Fact]
    public void Whole_input_scope_restores_every_field_after_cancellation()
    {
        var snapshot = State();
        var current = snapshot;
        var temporary = RenderedUiVirtualPointerPolicy.CreateMove(snapshot, 1400, 320);

        Assert.Throws<OperationCanceledException>(() =>
            RenderedUiVirtualPointerPolicy.ExecuteValueRestored<RenderedUiVirtualPointerInputState, bool>(
                snapshot,
                temporary,
                state => current = state,
                () => throw new OperationCanceledException()));

        Assert.Equal(snapshot, current);
    }

    [Theory]
    [InlineData("Character", "Other/49/5", 10, 10, 20, 20)]
    [InlineData("Character", "Character/49/5", 10, 10, 10, 20)]
    [InlineData("Character", "Character/49/5", 10, 20, 20, 20)]
    public void Target_validation_rejects_path_or_layout_mismatch(
        string addonName,
        string nodePath,
        float left,
        float top,
        float right,
        float bottom)
    {
        Assert.False(RenderedUiVirtualPointerPolicy.IsValidTarget(addonName, nodePath, left, top, right, bottom));
    }

    private static RenderedUiVirtualPointerInputState State() =>
        new(
            PositionX: 100,
            PositionY: 200,
            MouseWheel: -1,
            MouseButtonHeldFlags: 1,
            MouseButtonPressedFlags: 2,
            MouseButtonReleasedFlags: 4,
            MouseButtonHeldThrottledFlags: 8,
            DeltaX: 3,
            DeltaY: -5,
            IsGameWindowFocused: false);
}
