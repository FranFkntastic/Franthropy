using Franthropy.Dalamud.AgentBridge;

namespace Franthropy.Dalamud.Tests.AgentBridge;

public sealed class RenderedUiTextActionSelectorTests
{
    [Fact]
    public void Selects_smallest_registered_hit_target_covering_unique_text()
    {
        var result = RenderedUiTextActionSelector.Select(
            [new("NamePlate/20014/3", "NamePlate/20014", 272, 528, 411, 544)],
            [
                new("NamePlate/20014/11", "NamePlate/20014", 267, 520, 415, 544, RenderedUiClickDispatchMode.MouseDownUp),
                new("NamePlate/20014/12", "NamePlate/20014", 200, 400, 500, 600, RenderedUiClickDispatchMode.MouseClick),
            ]);

        Assert.True(result.Success);
        Assert.Equal("NamePlate/20014/11", result.TargetNodePath);
        Assert.Equal(RenderedUiClickDispatchMode.MouseDownUp, result.DispatchMode);
    }

    [Fact]
    public void Fails_closed_when_text_is_rendered_by_multiple_components()
    {
        var result = RenderedUiTextActionSelector.Select(
            [
                new("NamePlate/100/3", "NamePlate/100", 0, 0, 20, 10),
                new("NamePlate/200/3", "NamePlate/200", 0, 20, 20, 30),
            ],
            []);

        Assert.False(result.Success);
        Assert.Equal("RenderedTextAmbiguous", result.Code);
    }

    [Fact]
    public void Fails_closed_when_no_registered_hit_target_covers_text()
    {
        var result = RenderedUiTextActionSelector.Select(
            [new("NamePlate/100/3", "NamePlate/100", 0, 0, 20, 10)],
            [new("NamePlate/100/11", "NamePlate/100", 30, 30, 40, 40, RenderedUiClickDispatchMode.MouseDown)]);

        Assert.False(result.Success);
        Assert.Equal("RenderedHitTargetNotFound", result.Code);
    }

    [Fact]
    public void Selects_registered_ancestor_component_for_nested_button_text()
    {
        var result = RenderedUiTextActionSelector.Select(
            [new("Character/40/2", "Character/40", 100, 100, 140, 120)],
            [
                new("Character/41", "Character", 100, 100, 140, 120, RenderedUiClickDispatchMode.MouseDown),
                new("Character/40", "Character", 90, 90, 150, 130, RenderedUiClickDispatchMode.MouseClick),
            ]);

        Assert.True(result.Success);
        Assert.Equal("Character/40", result.TargetNodePath);
        Assert.Equal(RenderedUiClickDispatchMode.MouseClick, result.DispatchMode);
    }

    [Fact]
    public void SelectNearestLeft_uses_bounded_adjacent_control_and_excludes_farther_icons()
    {
        var result = RenderedUiTextActionSelector.SelectNearestLeft(
            [new("Character/15", "Character", 1452, 247, 1642, 262)],
            [
                new("Character/13/3", "Character", 1392, 251, 1420, 279, RenderedUiClickDispatchMode.MouseClick),
                new("Character/14/3", "Character", 1422, 251, 1450, 279, RenderedUiClickDispatchMode.MouseDownUp),
            ]);

        Assert.True(result.Success);
        Assert.Equal("Character/14/3", result.TargetNodePath);
        Assert.Equal(RenderedUiClickDispatchMode.MouseDownUp, result.DispatchMode);
    }

    [Fact]
    public void SelectNearestLeft_fails_closed_when_only_distant_controls_exist()
    {
        var result = RenderedUiTextActionSelector.SelectNearestLeft(
            [new("Character/15", "Character", 1452, 247, 1642, 262)],
            [new("Character/13/3", "Character", 1392, 251, 1420, 279, RenderedUiClickDispatchMode.MouseClick)]);

        Assert.False(result.Success);
        Assert.Equal("RenderedAdjacentTargetNotFound", result.Code);
    }
}
