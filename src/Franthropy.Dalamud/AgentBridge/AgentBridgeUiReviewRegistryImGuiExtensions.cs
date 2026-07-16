using Dalamud.Bindings.ImGui;

namespace Franthropy.Dalamud.AgentBridge;

public static class AgentBridgeUiReviewRegistryImGuiExtensions
{
    public static void RegisterLastButton(
        this AgentBridgeUiReviewRegistry registry,
        string id,
        string label,
        bool enabled,
        Action invoke,
        string? value = null) =>
        registry.RegisterLastItem(
            id,
            label,
            AgentBridgeUiControlKind.Button,
            enabled,
            selected: false,
            value,
            invoke);

    public static void RegisterLastSelectable(
        this AgentBridgeUiReviewRegistry registry,
        string id,
        string label,
        bool enabled,
        bool selected,
        Action invoke,
        string? value = null) =>
        registry.RegisterLastItem(
            id,
            label,
            AgentBridgeUiControlKind.Select,
            enabled,
            selected,
            value,
            invoke);

    public static void RegisterLastItem(
        this AgentBridgeUiReviewRegistry registry,
        string id,
        string label,
        AgentBridgeUiControlKind kind,
        bool enabled,
        bool selected,
        string? value,
        Action invoke)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(
            id,
            label,
            kind,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            enabled,
            selected,
            value,
            invoke);
    }
}
