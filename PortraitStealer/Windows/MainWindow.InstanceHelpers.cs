using System;
using System.Linq;
using System.Numerics;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Bindings.ImGui;
using OtterGui.Raii;
using OtterGui.Text;

namespace PortraitStealer.Windows;

public partial class MainWindow
{
    private bool DrawIconButton(FontAwesomeIcon icon, string label, Vector2 size, Action onClick)
    {
        using var style = ImRaii.PushStyle(
            ImGuiStyleVar.FramePadding,
            new Vector2(4f, 3f) * ImUtf8.GlobalScale
        );
        var text = label.Split("##")[0];
        var id = $"##{label.Split("##").Last()}";

        if (size == Vector2.Zero)
        {
            float iconWidth = ImUtf8.CalcIconSize(icon).X;
            float labelWidth = ImUtf8.CalcTextSize(text).X;
            size.X =
                iconWidth + ImUtf8.ItemInnerSpacing.X + labelWidth + ImUtf8.FramePadding.X * 2f;
            size.Y = ImUtf8.FrameHeight;
        }

        bool clicked = ImUtf8.Button(id, size);

        Vector2 itemMin = ImGui.GetItemRectMin();
        Vector2 iconSize = ImUtf8.CalcIconSize(icon);
        Vector2 textSize = ImUtf8.CalcTextSize(text);
        float totalContentWidth = iconSize.X + ImUtf8.ItemInnerSpacing.X + textSize.X;
        float startPosX =
            itemMin.X + Math.Max(ImUtf8.FramePadding.X, (size.X - totalContentWidth) * 0.5f);

        // Vertically center icon and text relative to the button height
        float maxContentHeight = Math.Max(iconSize.Y, textSize.Y);
        float startPosY = itemMin.Y + (size.Y - maxContentHeight) * 0.5f;
        float iconPosY = startPosY + (maxContentHeight - iconSize.Y) * 0.5f;
        float textPosY = startPosY + (maxContentHeight - textSize.Y) * 0.5f;

        // Draw icon and text using draw list so we don't disturb ImGui's last-item state
        var drawList = ImGui.GetWindowDrawList();

        // Icon
        using (var font = ImRaii.PushFont(UiBuilder.IconFont))
        {
            var iconPos = new Vector2(startPosX, iconPosY);
            drawList.AddText(iconPos, ImGui.GetColorU32(ImGuiCol.Text), icon.ToIconString());
        }

        // Text
        var textPos = new Vector2(startPosX + iconSize.X + ImUtf8.ItemInnerSpacing.X, textPosY);
        drawList.AddText(textPos, ImGui.GetColorU32(ImGuiCol.Text), text);

        if (clicked)
        {
            onClick?.Invoke();
        }
        return clicked;
    }



    private void DrawActionGroup(StolenPortraitInfo info, string idSuffix)
    {
        DrawCopyButtonAndStatus(info, idSuffix);
        ImGuiHelpers.ScaledDummy(MediumSpacing);
        DrawExplanationAndWarning(info);
    }

    private void DrawCopyButtonAndStatus(StolenPortraitInfo info, string idSuffix)
    {
        if (ImUtf8.Button($"Copy PortraitHelper String##Copy_{idSuffix}"))
        {
            var presetString = _plugin.GeneratePresetString(info);
            if (!string.IsNullOrEmpty(presetString))
            {
                ImUtf8.SetClipboardText(presetString);
                SetCopyStatus("Copied to clipboard!", false);
            }
            else
            {
                SetCopyStatus("Failed to generate string!", true);
            }
        }

        ImGui.SameLine(0, DefaultSpacing * ImUtf8.GlobalScale);

        bool showStatus =
            _copyStatusMessage != null
            && _copyStatusTimer.ElapsedMilliseconds < CopyStatusDurationMs;
        if (showStatus)
        {
            var icon = _copyStatusIsError
                ? FontAwesomeIcon.TimesCircle
                : FontAwesomeIcon.CheckCircle;
            var color = _copyStatusIsError ? ImGuiColors.DalamudRed : ImGuiColors.ParsedGreen;
            using var colorPush = ImRaii.PushColor(ImGuiCol.Text, color);

            DrawIcon(icon);
            ImGui.SameLine();
            ImUtf8.Text(_copyStatusMessage!);
        }
        else
        {
            ImGui.Dummy(new Vector2(_maxStatusSize.X, 0));
            if (_copyStatusMessage != null)
            {
                ClearCopyStatus();
            }
        }
    }

    private void ClearCopyStatus()
    {
        _copyStatusMessage = null;
        _copyStatusTimer.Stop();
    }

    private void SetCopyStatus(string message, bool isError)
    {
        _copyStatusMessage = message;
        _copyStatusIsError = isError;
        _copyStatusTimer.Restart();
    }
}
