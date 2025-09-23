using System;
using System.Numerics;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Bindings.ImGui;
using OtterGui.Raii;
using OtterGui.Text;
using Half = System.Half; // Required for GetCacheEntryStatus

namespace PortraitStealer.Windows;

// Partial class to contain STATIC UI helper methods
public partial class MainWindow
{
    // --- Static Helpers ---
    private static (FontAwesomeIcon Icon, Vector4 Color, string Tooltip) GetCacheEntryStatus(
        CachedPortraitData entry
    )
    {
        if (entry.FullPortraitData.HasValue)
        {
            bool looksZeroed =
                entry.FullPortraitData.Value.PortraitData.CameraZoom == (Half)0f
                && entry.FullPortraitData.Value.PortraitData.Expression == 0
                && entry.FullPortraitData.Value.PortraitData.CameraPosition.X == (Half)0f;

            if (looksZeroed)
            {
                return (
                    FontAwesomeIcon.ExclamationTriangle,
                    ImGuiColors.DalamudYellow,
                    "Data cached, but seems incomplete or zeroed.\\n(May happen if captured too quickly or during transitions)"
                );
            }
            else
            {
                return (
                    FontAwesomeIcon.CheckCircle,
                    ImGuiColors.ParsedGreen,
                    "Portrait data and image cached successfully."
                );
            }
        }
        else if (entry.NeedsFullDataFetchAttempt)
        {
            return (
                FontAwesomeIcon.HourglassHalf,
                ImGuiColors.DalamudGrey,
                "Waiting for fetch delay..."
            );
        }
        else
        {
            return (
                FontAwesomeIcon.TimesCircle,
                ImGuiColors.DalamudRed,
                "Full data fetch failed or was skipped.\\n(Portrait might not have been fully loaded in game)"
            );
        }
    }

    private static void DrawIcon(FontAwesomeIcon icon, Vector4? color = null)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        using var c = color.HasValue ? ImRaii.PushColor(ImGuiCol.Text, color.Value) : null;
        ImUtf8.Text(icon.ToIconString());
    }

    private static void DrawInstructions(string text)
    {
        using var color = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
        ImUtf8.TextWrapped(text);
    }

    private static void DrawSectionSeparator(string? text = null)
    {
        // Use constants defined in the main MainWindow partial class
        ImGuiHelpers.ScaledDummy(DefaultSpacing);
        if (string.IsNullOrEmpty(text))
        {
            ImGui.Separator();
        }
        else
        {
            using (var color = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImUtf8.Text(text);
            }
            ImGui.Separator();
        }
        ImGuiHelpers.ScaledDummy(MediumSpacing);
    }

    private static void DrawExplanationAndWarning(StolenPortraitInfo info)
    {
        using var childStyle = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 3f);
        using var childColor = ImRaii.PushColor(
            ImGuiCol.ChildBg,
            ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBg]
        );
        using var child = ImUtf8.Child(
            $"ExplanationChild_{info.ClassJobId}",
            new Vector2(0, 0),
            false,
            ImGuiWindowFlags.AlwaysUseWindowPadding
                | ImGuiWindowFlags.NoScrollbar
                | ImGuiWindowFlags.NoScrollWithMouse
                | ImGuiWindowFlags.AlwaysAutoResize
        );
        if (!child)
            return;

        using (var color = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
        {
            DrawIcon(FontAwesomeIcon.InfoCircle);
            ImGui.SameLine();
            ImUtf8.TextWrapped(
                "Import the copied string using PortraitHelper's clipboard import feature while editing a portrait."
            );
        }
        ImGuiHelpers.ScaledDummy(DefaultSpacing); // Use constant
        var jobAbbr = info.ClassJobAbbreviation;
        var warningText =
            $"Note: This portrait was captured on {jobAbbr}.\nIt might look incorrect if applied to a different job.";
        using (var color = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow))
        {
            DrawIcon(FontAwesomeIcon.ExclamationTriangle);
            ImGui.SameLine();
            ImUtf8.TextWrapped(warningText);
        }
    }

    private static void DrawInitialMessage(string message, float? availableHeight = null)
    {
        var region = availableHeight.HasValue
            ? new Vector2(ImGui.GetContentRegionAvail().X, availableHeight.Value)
            : ImGui.GetContentRegionAvail();
        if (region.X <= 0 || region.Y <= 0)
            return;

        using var color = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);

        // Calculate text size using the actual available width for wrapping
        var wrapWidth = region.X - ImGui.GetStyle().WindowPadding.X * 2; // Account for padding
        var textSize = ImGui.CalcTextSize(message, true, wrapWidth);

        // Calculate starting position for vertical centering
        float startY = ImGui.GetCursorPosY() + Math.Max(0, (region.Y - textSize.Y) * 0.5f);

        // Calculate starting X for horizontal centering
        float startX = ImGui.GetCursorPosX() + Math.Max(0, (region.X - textSize.X) * 0.5f);

        // Draw Text (Centered horizontally within the block)
        ImGui.SetCursorPos(new Vector2(startX, startY));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrapWidth); // Use calculated wrap width
        ImUtf8.TextWrapped(message);
        ImGui.PopTextWrapPos();
    }


}
