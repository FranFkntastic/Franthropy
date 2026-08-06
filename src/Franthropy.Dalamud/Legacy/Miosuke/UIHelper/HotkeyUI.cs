using System.Numerics;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;
using Miosuke.Action;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Interface.Utility;
using Miosuke.Extensions;


namespace Miosuke.UiHelper;

public class HotkeyUi
{
    public bool doSetInputFocused = false;
    public bool isEditingHotkey = false;
    public List<VirtualKey> userHotkeyList = [];

    public bool DrawConfigUi(string id, ref VirtualKey[] hotkey, float width = 150f)
    {
        var imguiId = ImRaii.PushId(id);

        var isHotkeyChanged = false;

        // update user hotkey from user input
        if (isEditingHotkey)
        {
            if (ImGui.GetIO().KeyAlt && !userHotkeyList.Contains(VirtualKey.MENU)) userHotkeyList.Add(VirtualKey.MENU);
            if (ImGui.GetIO().KeyShift && !userHotkeyList.Contains(VirtualKey.SHIFT)) userHotkeyList.Add(VirtualKey.SHIFT);
            if (ImGui.GetIO().KeyCtrl && !userHotkeyList.Contains(VirtualKey.CONTROL)) userHotkeyList.Add(VirtualKey.CONTROL);

            for (var i = 0; i < ImGui.GetIO().KeysDown.Length && i < 160; i++)
            {
                if (ImGui.GetIO().KeysDown[i])
                {
                    var vkey = (VirtualKey)i;

                    if (vkey == VirtualKey.ESCAPE)
                    {
                        // cancel editing
                        isEditingHotkey = false;
                        break;
                    }

                    if (!userHotkeyList.Contains(vkey))
                    {
                        userHotkeyList.Add(vkey);
                    }
                }
            }

            userHotkeyList.Sort();
        }

        // draw hotkey input bar
        var hotkeyString = isEditingHotkey ? userHotkeyList.HotkeyToString() : hotkey.HotkeyToString();
        var buttonWidth = ImGui.CalcTextSize("Cancel").X + ImGui.GetStyle().FramePadding.X * 2;
        var inputWidth = width
            - ImGui.GetStyle().ItemSpacing.X
            - buttonWidth;
        DrawHotkeyInput(id, ref hotkeyString, inputWidth, out var isInputActive);

        // buttons and actions
        ImGui.SameLine();
        // ImGui.SetNextItemWidth(buttonWidth);
        if (!isEditingHotkey)
        {
            if (ImGui.Button($"Edit", new Vector2(buttonWidth, 0)))
            {
                // start editing
                isEditingHotkey = true;
                userHotkeyList = [];
            }
        }
        else
        {
            if (userHotkeyList.Count > 0)
            {
                // save and stop editing
                if (ImGui.Button($"Save", new Vector2(buttonWidth, 0)))
                {
                    isEditingHotkey = false;
                    isHotkeyChanged = true;
                    hotkey = [.. userHotkeyList];
                }
            }
            else
            {
                // if no hotkey, show cancel button, cancel editing
                if (ImGui.Button($"Cancel", new Vector2(buttonWidth, 0)))
                {
                    isEditingHotkey = false;
                }
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35f);
            ImGui.TextUnformatted(
                "Click 'Edit' to set a new hotkey.\n" +
                "During editing:\n" +
                "- Press ESC on your keyboard to cancel.\n" +
                "- Click 'Save' to save."
            );
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        // when editing, if buttons are not being clicked, and if input is not active, set focus
        if (isEditingHotkey && !ImGui.IsItemFocused() && !isInputActive)
        {
            doSetInputFocused = true;
        }

        imguiId.Pop();

        return isHotkeyChanged;
    }

    private void DrawHotkeyInput(string id, ref string hotkeyString, float inputWidth, out bool isInputActive)
    {
        // set style
        if (isEditingHotkey)
        {
            ImGui.PushStyleColor(ImGuiCol.Border, 0xFF00A5FF);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 2);
        }

        ImGui.SetNextItemWidth(inputWidth);
        ImGui.InputText($"##hotkeyString", ref hotkeyString, 100, ImGuiInputTextFlags.ReadOnly);

        // set focus when required
        if (doSetInputFocused)
        {
            doSetInputFocused = false;
            ImGui.SetKeyboardFocusHere(-1);
        }
        isInputActive = ImGui.IsItemActive();

        // reset style
        if (isEditingHotkey)
        {
            ImGui.PopStyleColor(1);
            ImGui.PopStyleVar();
        }
    }
}
