using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace PortraitStealer.Windows;

internal static class ImUtf8
{
    public static ImGuiStylePtr Style
        => ImGui.GetStyle();

    public static Vector2 ItemInnerSpacing
        => Style.ItemInnerSpacing;

    public static Vector2 FramePadding
        => Style.FramePadding;

    public static float GlobalScale
        => ImGui.GetIO().FontGlobalScale;

    public static float FrameHeight
        => ImGui.GetFrameHeight();

    public static float TextHeightSpacing
        => ImGui.GetTextLineHeightWithSpacing();

    public static ImRaii.IEndObject TabBar(string label, ImGuiTabBarFlags flags = ImGuiTabBarFlags.None)
        => ImRaii.TabBar(label, flags);

    public static ImRaii.IEndObject TabItem(string label, ImGuiTabItemFlags flags = ImGuiTabItemFlags.None)
        => ImRaii.TabItem(label, flags);

    public static ImRaii.IEndObject Child(
        string id,
        Vector2 size,
        bool border = false,
        ImGuiWindowFlags flags = ImGuiWindowFlags.None)
        => ImRaii.Child(id, size, border, flags);

    public static bool Button(string label, Vector2 size = default)
        => ImGui.Button(label, size);

    public static bool Selectable(
        string label,
        bool isSelected = false,
        ImGuiSelectableFlags flags = ImGuiSelectableFlags.None,
        Vector2 size = default)
        => ImGui.Selectable(label, isSelected, flags, size);

    public static void Text(string text)
        => ImGui.TextUnformatted(text);

    public static void Text(ReadOnlySpan<byte> text)
        => ImGui.TextUnformatted(text);

    public static void TextWrapped(string text, float wrapPos = 0f)
    {
        using var _ = ImRaii.TextWrapPos(wrapPos);
        Text(text);
    }

    public static void TextWrapped(ReadOnlySpan<byte> text, float wrapPos = 0f)
    {
        using var _ = ImRaii.TextWrapPos(wrapPos);
        Text(text);
    }

    public static Vector2 CalcTextSize(string text, bool hideTextAfterDashes = true, float wrapWidth = 0f)
        => ImGui.CalcTextSize(text, hideTextAfterDashes, wrapWidth);

    public static Vector2 CalcIconSize(FontAwesomeIcon icon)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        return ImGui.CalcTextSize(icon.ToIconString(), true);
    }

    public static void SetClipboardText(string text)
        => ImGui.SetClipboardText(text);

    public static void HoverTooltip(ReadOnlySpan<byte> text)
        => HoverTooltip(ImGuiHoveredFlags.None, text);

    public static void HoverTooltip(string text)
        => HoverTooltip(ImGuiHoveredFlags.None, text);

    public static void HoverTooltip(ImGuiHoveredFlags flags, ReadOnlySpan<byte> text)
    {
        if (text.IsEmpty || text[0] == 0 || !ImGui.IsItemHovered(flags))
            return;

        using var _ = ImRaii.Enabled();
        using var tt = ImRaii.Tooltip();
        Text(text);
    }

    public static void HoverTooltip(ImGuiHoveredFlags flags, string text)
    {
        if (string.IsNullOrEmpty(text) || !ImGui.IsItemHovered(flags))
            return;

        using var _ = ImRaii.Enabled();
        using var tt = ImRaii.Tooltip();
        Text(text);
    }

    public static bool Spinner(ReadOnlySpan<byte> label, float radius, int thickness, uint color)
    {
        var style = ImGui.GetStyle();
        var pos = ImGui.GetCursorScreenPos();
        var size = new Vector2(radius * 2f, (radius + style.FramePadding.Y) * 2f);

        ImGui.InvisibleButton(label, size);
        if (!ImGui.IsItemVisible())
            return false;

        var drawList = ImGui.GetWindowDrawList();
        drawList.PathClear();
        const float numSegments = 30f;
        var time = (float)ImGui.GetTime();
        var start = MathF.Abs(MathF.Sin(time * 1.8f) * (numSegments - 5f));
        var max = MathF.PI * 2f * (numSegments - 3f) / numSegments;
        var min = MathF.PI * 2f * start / numSegments;
        var diff = max - min;
        var center = new Vector2(pos.X + radius, pos.Y + radius + style.FramePadding.Y);

        for (var i = 0; i < numSegments; ++i)
        {
            var segment = min + i / numSegments * diff + time * 8f;
            drawList.PathLineTo(
                new Vector2(center.X + MathF.Cos(segment) * radius, center.Y + MathF.Sin(segment) * radius)
            );
        }

        drawList.PathStroke(color, ImDrawFlags.None, thickness);
        return true;
    }
}
