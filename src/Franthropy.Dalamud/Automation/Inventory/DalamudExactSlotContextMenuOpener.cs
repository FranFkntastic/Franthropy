using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Franthropy.Dalamud.Equipment;

namespace Franthropy.Dalamud.Automation.Inventory;

/// <summary>
/// Stages the normal inventory owner before asking AgentInventoryContext to present an
/// exact slot's menu. Opening both in one frame allows the owner's initialization to
/// immediately dismiss the newly created context menu.
/// </summary>
public sealed class DalamudExactSlotContextMenuOpener
{
    private readonly DalamudUiStabilityGate ownerStability;
    private string? targetContainer;
    private int targetSlot;
    private InventoryType inventoryType;
    private AgentId ownerId;
    private uint stableAddonId;
    private bool begun;
    private bool ownerResetRequested;
    private bool contextMenuRequested;

    public string Status { get; private set; } = "Idle.";

    public DalamudExactSlotContextMenuOpener(int requiredStableFrames = 6)
    {
        if (requiredStableFrames <= 0)
            throw new ArgumentOutOfRangeException(nameof(requiredStableFrames));
        ownerStability = new DalamudUiStabilityGate(requiredStableFrames);
    }

    public unsafe DalamudUiTransactionResult Begin(EquipmentInstanceFingerprint fingerprint)
    {
        Reset();
        if (!Enum.TryParse(fingerprint.Container, out inventoryType))
            return DalamudUiTransactionResult.Fail("UnsupportedContainer", $"Inventory container {fingerprint.Container} is not recognized.");
        if (AgentInventoryContext.Instance() == null)
            return DalamudUiTransactionResult.Fail("InventoryContextUnavailable", "Inventory context UI is unavailable.");

        ownerId = inventoryType.ToString().StartsWith("Armory", StringComparison.Ordinal)
            ? AgentId.ArmouryBoard
            : AgentId.Inventory;
        var owner = AgentModule.Instance()->GetAgentByInternalId(ownerId);
        if (owner == null)
            return DalamudUiTransactionResult.Fail("InventoryOwnerUnavailable", $"The normal {ownerId} UI is unavailable.");

        targetContainer = fingerprint.Container;
        targetSlot = fingerprint.SlotIndex;
        begun = true;
        if (owner->IsAgentActive() && !IsOwnerAddonPresented(owner->GetAddonId()))
        {
            // AgentInventory can retain its active bit after its addon has been
            // hidden. Show is a no-op in that state, so first let Hide clear the
            // stale ownership and present it again on a later framework frame.
            owner->Hide();
            ownerResetRequested = true;
            Status = $"Reset the stale {ownerId} agent; waiting to present its normal item window.";
        }
        else if (!owner->IsAgentActive())
        {
            owner->Show();
            Status = $"Requested the normal {ownerId} item window; waiting for its addon ownership to stabilize.";
        }
        else
        {
            Status = $"The normal {ownerId} item window is presented; waiting for its addon ownership to stabilize.";
        }
        return DalamudUiTransactionResult.Completed("InventoryOwnerRequested", Status);
    }

    public unsafe DalamudUiTransactionResult Advance(EquipmentInstanceFingerprint fingerprint)
    {
        if (!begun || !string.Equals(targetContainer, fingerprint.Container, StringComparison.Ordinal) || targetSlot != fingerprint.SlotIndex)
            return DalamudUiTransactionResult.Fail("ContextMenuTargetChanged", "The exact-slot context-menu target changed while its owner UI was being prepared.");
        if (contextMenuRequested)
            return DalamudUiTransactionResult.Completed("ContextMenuRequested", Status);

        var context = AgentInventoryContext.Instance();
        var owner = AgentModule.Instance()->GetAgentByInternalId(ownerId);
        if (context == null || owner == null)
            return DalamudUiTransactionResult.Fail("InventoryContextUnavailable", "The normal inventory owner or context agent disappeared while preparing the item menu.");
        if (ownerResetRequested)
        {
            if (owner->IsAgentActive())
            {
                Status = $"Waiting for the stale {ownerId} agent to release its hidden addon.";
                return DalamudUiTransactionResult.Pending(Status);
            }
            ownerResetRequested = false;
            owner->Show();
            Status = $"Requested the normal {ownerId} item window after resetting its stale agent state.";
            return DalamudUiTransactionResult.Pending(Status);
        }
        if (!owner->IsAgentActive())
        {
            ownerStability.Observe(false);
            stableAddonId = 0;
            owner->Show();
            Status = $"Waiting for the normal {ownerId} item window to become active.";
            return DalamudUiTransactionResult.Pending(Status);
        }

        var addonId = owner->GetAddonId();
        if (addonId == 0)
        {
            ownerStability.Observe(false);
            stableAddonId = 0;
            Status = $"The {ownerId} agent is active; waiting for its presented addon ID.";
            return DalamudUiTransactionResult.Pending(Status);
        }
        if (!IsOwnerAddonPresented(addonId))
        {
            ownerStability.Observe(false);
            stableAddonId = 0;
            Status = $"The {ownerId} agent is active, but its addon is not presented yet.";
            return DalamudUiTransactionResult.Pending(Status);
        }
        if (stableAddonId != addonId)
        {
            ownerStability.Reset();
            stableAddonId = addonId;
        }
        if (!ownerStability.Observe(true))
        {
            Status = $"The {ownerId} owner is active on addon {addonId}; verifying stability ({ownerStability.ObservedConsecutiveFrames}/{ownerStability.RequiredConsecutiveFrames} frames).";
            return DalamudUiTransactionResult.Pending(Status);
        }

        context->OpenForItemSlot(inventoryType, fingerprint.SlotIndex, 0, addonId);
        contextMenuRequested = true;
        Status = $"Requested the exact slot's context menu after {ownerId} remained stable for {ownerStability.ObservedConsecutiveFrames} frames.";
        return DalamudUiTransactionResult.Completed("ContextMenuRequested", Status);
    }

    public void Reset()
    {
        targetContainer = null;
        targetSlot = 0;
        inventoryType = default;
        ownerId = default;
        stableAddonId = 0;
        begun = false;
        ownerResetRequested = false;
        contextMenuRequested = false;
        ownerStability.Reset();
        Status = "Idle.";
    }

    private static unsafe bool IsOwnerAddonPresented(uint addonId)
    {
        if (addonId == 0)
            return false;
        var unitManager = RaptureAtkUnitManager.Instance();
        var addon = unitManager == null ? null : unitManager->GetAddonById((ushort)addonId);
        return addon != null && addon->IsReady && addon->IsVisible;
    }
}
