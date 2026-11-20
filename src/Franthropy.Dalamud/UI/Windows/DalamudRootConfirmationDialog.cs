using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.UI.Styling;

namespace Franthropy.Dalamud.UI.Windows;

/// <summary>How a confirmation dialog styles its confirm button.</summary>
public enum DalamudConfirmationEmphasis
{
    /// <summary>The style's ordinary button.</summary>
    None,

    /// <summary>The palette's accent, for a consequential but recoverable action.</summary>
    Primary,

    /// <summary>The fixed red danger colours, for a destructive action.</summary>
    Destructive,
}

/// <summary>A dialog button, reported to <see cref="DalamudRootConfirmationDialog.ControlSubmitted"/> right after it is drawn.</summary>
public enum DalamudConfirmationControl
{
    Cancel,
    Confirm,
}

/// <summary>One pending confirmation.</summary>
/// <param name="Id">Stable identity for the confirmed action; scopes the popup and its button IDs.</param>
/// <param name="Title">Modal title-bar text.</param>
/// <param name="Message">Wrapped body text.</param>
/// <param name="ConfirmLabel">Text of the confirm button.</param>
/// <param name="Confirm">Runs on confirmation. An exception keeps the dialog open and shows its message.</param>
public sealed record DalamudConfirmationRequest(
    string Id,
    string Title,
    string Message,
    string ConfirmLabel,
    Action Confirm)
{
    public string CancelLabel { get; init; } = "Cancel";

    public DalamudConfirmationEmphasis Emphasis { get; init; } = DalamudConfirmationEmphasis.Destructive;
}

/// <summary>
/// Hosts confirmation modals at the root window level. Call <see cref="Request(DalamudConfirmationRequest)"/>
/// from any child, table, or popup, then call <see cref="Draw"/> once per frame after those containers
/// have ended, still inside the root window.
/// </summary>
/// <remarks>
/// A modal opened inside a child, table, or popup can be clipped or never submitted while it still
/// captures input, freezing the whole window. Deferring the open to the root scope avoids that trap.
/// The modal is pinned to the root window's viewport, centred on it, and kept above it.
/// Enter confirms and Escape cancels while the modal is focused.
/// </remarks>
public sealed class DalamudRootConfirmationDialog
{
    private readonly string idScope;
    private readonly DalamudUiPalette? palette;
    private DalamudConfirmationRequest? pending;
    private DalamudConfirmationControl? queued;
    private bool openRequested;
    private string? shownPopupId;
    private string error = string.Empty;

    /// <param name="idScope">Distinguishes this dialog's popup IDs from others in the same window.</param>
    /// <param name="palette">Colours for <see cref="DalamudConfirmationEmphasis.Primary"/> and the error line;
    /// derived from the style's check-mark colour when null.</param>
    public DalamudRootConfirmationDialog(string idScope = "FranthropyConfirm", DalamudUiPalette? palette = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idScope);
        this.idScope = idScope;
        this.palette = palette;
    }

    /// <summary>Called right after each dialog button is drawn, so the caller can register it as the last item
    /// (for example with an agent bridge review registry). Pair an external invocation with
    /// <see cref="QueueConfirm"/> or <see cref="QueueCancel"/>.</summary>
    public Action<DalamudConfirmationControl, DalamudConfirmationRequest>? ControlSubmitted { get; set; }

    /// <summary>Upper bound for the message's line width; 28 font-heights when null.</summary>
    public float? MessageWrapWidth { get; set; }

    /// <summary>The confirmation awaiting an answer, if any.</summary>
    public DalamudConfirmationRequest? Pending => pending;

    public bool IsPending => pending is not null;

    /// <summary>Replaces any pending confirmation and opens the modal on the next <see cref="Draw"/>.</summary>
    public void Request(DalamudConfirmationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Message);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConfirmLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CancelLabel);
        ArgumentNullException.ThrowIfNull(request.Confirm);

        pending = request;
        queued = null;
        openRequested = true;
        error = string.Empty;
    }

    /// <inheritdoc cref="Request(DalamudConfirmationRequest)"/>
    public void Request(
        string id,
        string title,
        string message,
        string confirmLabel,
        Action confirm,
        DalamudConfirmationEmphasis emphasis = DalamudConfirmationEmphasis.Destructive) =>
        Request(new DalamudConfirmationRequest(id, title, message, confirmLabel, confirm) { Emphasis = emphasis });

    /// <summary>Confirms on the next <see cref="Draw"/>, as if the confirm button were clicked.</summary>
    /// <remarks>Safe to call outside the ImGui frame; the modal must close from inside its own scope.</remarks>
    public void QueueConfirm()
    {
        if (pending is not null)
            queued = DalamudConfirmationControl.Confirm;
    }

    /// <summary>Cancels on the next <see cref="Draw"/>, as if the cancel button were clicked.</summary>
    public void QueueCancel()
    {
        if (pending is not null)
            queued = DalamudConfirmationControl.Cancel;
    }

    /// <summary>Drops the pending confirmation without running it. The modal closes on the next <see cref="Draw"/>.</summary>
    public void Clear()
    {
        pending = null;
        queued = null;
        openRequested = false;
        error = string.Empty;
    }

    /// <summary>Draws the pending modal. Call once per frame from the root window scope.</summary>
    public void Draw()
    {
        if (pending is not { } request)
        {
            CloseOrphanedModal();
            return;
        }

        var popupId = $"{request.Title}##{idScope}:{request.Id}";
        if (openRequested)
        {
            // Opening from the root scope replaces any modal this dialog showed before.
            ImGui.OpenPopup(popupId);
            openRequested = false;
        }
        shownPopupId = popupId;

        var open = true;
        if (!DalamudOwnedPopup.BeginOwnedModal(popupId, ref open))
        {
            // Closed from the title bar, or by ImGui (e.g. another root popup took its place).
            if (!open || !ImGui.IsPopupOpen(popupId))
                Clear();
            return;
        }

        var resolved = palette ?? DalamudUiPalette.Dark(ImGui.GetStyle().Colors[(int)ImGuiCol.CheckMark]);
        var wrapWidth = MessageWrapWidth ?? (ImGui.GetFontSize() * 28f);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrapWidth);
        ImGui.TextUnformatted(request.Message);
        if (!string.IsNullOrWhiteSpace(error))
            ImGui.TextColored(resolved.Error, error);
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        var answer = queued;
        queued = null;
        // Keys pressed on the frame the modal appears belong to whatever opened it.
        if (answer is null && !ImGui.IsWindowAppearing() && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
                answer = DalamudConfirmationControl.Cancel;
            else if (ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false))
                answer = DalamudConfirmationControl.Confirm;
        }

        if (ImGui.Button($"{request.CancelLabel}##{idScope}Cancel:{request.Id}"))
            answer = DalamudConfirmationControl.Cancel;
        ControlSubmitted?.Invoke(DalamudConfirmationControl.Cancel, request);

        ImGui.SameLine();
        using (PushEmphasis(request.Emphasis, resolved))
        {
            if (ImGui.Button($"{request.ConfirmLabel}##{idScope}Accept:{request.Id}"))
                answer = DalamudConfirmationControl.Confirm;
        }
        ControlSubmitted?.Invoke(DalamudConfirmationControl.Confirm, request);

        if (answer == DalamudConfirmationControl.Cancel)
        {
            Clear();
            ImGui.CloseCurrentPopup();
        }
        else if (answer == DalamudConfirmationControl.Confirm)
        {
            try
            {
                request.Confirm();
                // Confirm may have requested a follow-up confirmation; keep it.
                if (ReferenceEquals(pending, request))
                    Clear();
                ImGui.CloseCurrentPopup();
            }
            catch (Exception exception)
            {
                error = exception.Message;
            }
        }

        ImGui.EndPopup();
        if (!open && ReferenceEquals(pending, request))
            Clear();
    }

    private void CloseOrphanedModal()
    {
        if (shownPopupId is not { } popupId)
            return;

        shownPopupId = null;
        // Cleared from outside the modal's scope: submit it once more so it can close itself
        // instead of lingering in the popup stack.
        if (ImGui.IsPopupOpen(popupId) && DalamudOwnedPopup.BeginOwnedModal(popupId))
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    private static IDisposable? PushEmphasis(DalamudConfirmationEmphasis emphasis, DalamudUiPalette palette) =>
        emphasis switch
        {
            DalamudConfirmationEmphasis.Destructive => DalamudUiChrome.PushDangerButton(),
            DalamudConfirmationEmphasis.Primary => DalamudUiChrome.PushButton(palette),
            _ => null,
        };
}
