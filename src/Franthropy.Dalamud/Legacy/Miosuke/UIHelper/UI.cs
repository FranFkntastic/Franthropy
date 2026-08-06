#pragma warning disable CA2211 // Non-constant fields should not be visible

using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Game.Text.SeStringHandling;

namespace Miosuke.UiHelper;

public static class Ui
{
    // COLOUR

    public static readonly Vector4 ColourAccentDark = HslaToDecimal(25, 0.45, 0.25);
    public static readonly Vector4 ColourAccentLight = HslaToDecimal(35, 0.75, 0.85);
    public static readonly Vector4 ColourAccentLightAlt = HslaToDecimal(25, 0.75, 0.85);
    public static readonly Vector4 ColourBackground = HslaToDecimal(35, 0.8, 0.82);
    public static readonly Vector4 ColourBackground1 = HslaToDecimal(35, 0.60, 0.77);
    public static readonly Vector4 ColourBackground1Active = HslaToDecimal(35, 0.60, 0.76);
    public static readonly Vector4 ColourBackground1Hover = HslaToDecimal(35, 0.60, 0.78);
    public static readonly Vector4 ColourBackground2 = HslaToDecimal(35, 0.55, 0.72);
    public static readonly Vector4 ColourBackground3 = HslaToDecimal(35, 0.50, 0.68);
    public static readonly Vector4 ColourBackground4 = HslaToDecimal(35, 0.45, 0.62);
    public static readonly Vector4 ColourBackgroundAlt = HslaToDecimal(27, 0.45, 0.70);
    public static readonly Vector4 ColourForeground = HslaToDecimal(20, 0.35, 0.35);
    public static readonly Vector4 ColourForegroundComment = HslaToDecimal(25, 0.35, 0.50);
    public static readonly Vector4 ColourInputHint = HslaToDecimal(35, 0.25, 0.65);

    public static readonly Vector4 ColourBlack = new(0, 0, 0, 1);
    public static readonly Vector4 ColourWhite = new(1, 1, 1, 1);
    public static readonly Vector4 ColourWhiteDim = new(0.95f, 0.95f, 0.95f, 1);
    public static readonly Vector4 ColourWhite2 = new(1, 1, 1, 0.85f);
    public static readonly Vector4 ColourWhite3 = new(1, 1, 1, 0.65f);
    public static readonly Vector4 ColourWhite4 = new(1, 1, 1, 0.50f);
    public static readonly Vector4 ColourCyan = HslaToDecimal(175, 0.5, 0.75);
    public static readonly Vector4 ColourCyanDark = HslaToDecimal(190, 0.50, 0.35);
    public static readonly Vector4 ColourBlue = HslaToDecimal(200, 0.85, 0.6);
    public static readonly Vector4 ColourSkyBlue = HslaToDecimal(175, 0.25, 0.66);
    public static readonly Vector4 ColourSkyBlueActive = HslaToDecimal(175, 0.25, 0.64);
    public static readonly Vector4 ColourSkyBlueHover = HslaToDecimal(175, 0.25, 0.68);
    public static readonly Vector4 ColourCrimson = HslaToDecimal(5, 0.75, 0.6);
    public static readonly Vector4 ColourKhaki = HslaToDecimal(25, 0.65, 0.75);
    public static readonly Vector4 ColourPink = HslaToDecimal(0, 1, 0.85f);
    public static readonly Vector4 ColourYellow = HslaToDecimal(50, 1, 0.90f);
    public static readonly Vector4 ColourMale = HslaToDecimal(200, 0.80, 0.60);
    public static readonly Vector4 ColourFemale = HslaToDecimal(340, 0.80, 0.70);



    public static readonly Vector4 ColourHq = HslaToDecimal(45, 0.80, 0.70);
    public static readonly Vector4 ColourGil = HslaToDecimal(35, 0.75, 0.40);



    public static readonly Vector4 Alpha = new(1, 1, 1, 0.55f);
    public static readonly Vector4 Alpha2 = new(1, 1, 1, 0.45f);
    public static readonly Vector4 AlphaLight = new(1, 1, 1, 0.95f);
    public static readonly Vector4 AlphaLight2 = new(1, 1, 1, 0.85f);
    public static readonly Vector4 AlphaLight3 = new(1, 1, 1, 0.80f);
    public static readonly Vector4 AlphaLight4 = new(1, 1, 1, 0.70f);
    public static readonly Vector4 Transparent = new(1, 1, 1, 0);


    // STYLE

    public static readonly Vector2 FramePadding = new(8f, 8f);
    public static readonly Vector2 ItemSpacing = new(6f, 6f);
    public static readonly Vector2 WindowPadding = new(5f, 5f);
    public static readonly float FrameRounding = 10.0f;
    public static readonly float FrameBorderSize = 3.0f;
    public static readonly float ScrollbarSize = 7.0f;
    public static readonly float WindowRounding = 10.0f;
    public static readonly float WindowBorderSize = 3.0f;
    public static void PushStyleCollection(ref ImRaii.StyleDisposable style, ref ImRaii.ColorDisposable styleColour)
    {
        style
            .Push(ImGuiStyleVar.FramePadding, FramePadding)
            .Push(ImGuiStyleVar.ItemSpacing, ItemSpacing)
            .Push(ImGuiStyleVar.WindowPadding, WindowPadding)
            .Push(ImGuiStyleVar.FrameRounding, FrameRounding)
            .Push(ImGuiStyleVar.FrameBorderSize, FrameBorderSize)
            .Push(ImGuiStyleVar.ScrollbarSize, ScrollbarSize)
            .Push(ImGuiStyleVar.WindowRounding, WindowRounding)
            .Push(ImGuiStyleVar.WindowBorderSize, WindowBorderSize)
            .Push(ImGuiStyleVar.PopupRounding, WindowRounding * 0.5f)
            .Push(ImGuiStyleVar.PopupBorderSize, WindowBorderSize * 0.75f);
        styleColour
            .Push(ImGuiCol.Border, ColourAccentLight)
            .Push(ImGuiCol.ScrollbarGrab, ColourAccentDark)
            .Push(ImGuiCol.FrameBg, ColourBackground)
            .Push(ImGuiCol.FrameBgActive, ColourBackground)
            .Push(ImGuiCol.WindowBg, ColourBackground)
            .Push(ImGuiCol.PopupBg, ColourBackground1);
    }






    // UI COMPONENTS

    public static void TextUrlWithLabelButton(string url)
    {
        ImGui.Text(url);

        ImGui.SameLine();
        if (ImGui.SmallButton($"Copy"))
        {
            ImGui.SetClipboardText(url);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Copy to clipboard");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton($"Open"))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Open in browser");
        }
    }


    // IMGUI SHORTCUTS

    public static void AlignRight(string text)
    {
        var posX = ImGui.GetCursorPosX()
            + ImGui.GetColumnWidth()
            - ImGui.CalcTextSize(text).X
            - ImGui.GetScrollX()
            - (1 * ImGui.GetStyle().ItemSpacing.X);
        ImGui.SetCursorPosX(posX);
    }


    // FFXIV HELPERS

    public static void RenderSeString(SeString seString)
    {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetStyle().ItemSpacing.X);
        foreach (var payload in seString.Payloads)
        {
            if (payload is TextPayload textPayload)
            {
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() - ImGui.GetStyle().ItemSpacing.X);
                // show \n as plain text
                var plain_text = textPayload.Text?.Replace("\n", "\\n");
                ImGui.Text(plain_text);
                ImGui.SameLine();
            }
            else if (payload is UIForegroundPayload uiForegroundPayload)
            {
                if (uiForegroundPayload.IsEnabled)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, ColourUintToDecimal(uiForegroundPayload.UIColor.Value.Dark));
                }
                else
                {
                    ImGui.PopStyleColor();
                }
            }
        }
    }

    private static Vector4 ColourUintToDecimal(uint color)
    {
        var r = (byte)(color >> 24);
        var g = (byte)(color >> 16);
        var b = (byte)(color >> 8);
        var a = (byte)color;

        return new Vector4(r / 255.0f, g / 255.0f, b / 255.0f, a / 255.0f);
    }


    // COLOUR

    /// <summary>
    /// (255, 255, 255, 1) -> (1, 1, 1, 1)
    /// </summary>
    public static Vector4 RgbaToDecimal(byte r, byte g, byte b, byte a)
    {
        return new Vector4(r / 255f, g / 255f, b / 255f, a / 1f);
    }

    /// <summary>
    /// (360, 1, 1, 1) -> (1, 1, 1, 1)
    /// </summary>
    public static Vector4 HslaToDecimal(double h, double s, double l, double a = 1.0)
    {
        var rgba = HslaToRgba(h, s, l, a);
        return new Vector4(rgba.X / 255f, rgba.Y / 255f, rgba.Z / 255f, rgba.W / 1f);
    }

    /// <summary>
    /// (360, 1, 1, 1) -> (255, 255, 255, 1)
    /// </summary>
    public static Vector4 HslaToRgba(double h, double s, double l, double a = 1.0)
    {
        double v;
        double r, g, b;

        h /= 360.0;

        r = l;   // default to gray
        g = l;
        b = l;
        v = (l <= 0.5) ? (l * (1.0 + s)) : (l + s - l * s);

        if (v > 0)
        {
            double m;
            double sv;
            int sextant;
            double fract, vsf, mid1, mid2;

            m = l + l - v;
            sv = (v - m) / v;
            h *= 6.0;
            sextant = (int)h;
            fract = h - sextant;
            vsf = v * sv * fract;
            mid1 = m + vsf;
            mid2 = v - vsf;

            switch (sextant)
            {
                case 0:
                    r = v;
                    g = mid1;
                    b = m;
                    break;
                case 1:
                    r = mid2;
                    g = v;
                    b = m;
                    break;
                case 2:
                    r = m;
                    g = v;
                    b = mid1;
                    break;
                case 3:
                    r = m;
                    g = mid2;
                    b = v;
                    break;
                case 4:
                    r = mid1;
                    g = m;
                    b = v;
                    break;
                case 5:
                    r = v;
                    g = m;
                    b = mid2;
                    break;
            }
        }
        return new Vector4((float)r * 255, (float)g * 255, (float)b * 255, (float)a);
    }
}
