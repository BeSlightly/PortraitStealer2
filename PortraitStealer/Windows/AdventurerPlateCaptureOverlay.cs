using System;
using System.Numerics;
using Dalamud.Game.NativeWrapper;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PortraitStealer.Windows;

public sealed class AdventurerPlateCaptureOverlay : Window, IDisposable
{
    private readonly Plugin _plugin;

    public AdventurerPlateCaptureOverlay(Plugin plugin)
        : base("###PortraitStealer_AdventurerPlateCaptureOverlay")
    {
        _plugin = plugin;

        IsOpen = true;
        Flags =
            ImGuiWindowFlags.NoDecoration
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.AlwaysAutoResize;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    public override bool DrawConditions()
    {
        if (!_plugin.Configuration.ShowCaptureButtonOnAdventurerPlate)
            return false;

        try
        {
            var agentInterface = Plugin.GameGui.GetAgentById((int)AgentId.CharaCard);
            if (agentInterface.IsNull || !agentInterface.IsAgentActive)
                return false;
        }
        catch
        {
            return false;
        }

        return TryGetCharaCardAddon(out var addon) && addon.IsReady && addon.IsVisible;
    }

    public override void Draw()
    {
        Position = SnapToCharaCard();

        if (_plugin.TryConsumePendingAdventurerPlateCopy(out var pendingInfo))
        {
            var presetString = _plugin.GeneratePresetString(pendingInfo);
            if (!string.IsNullOrEmpty(presetString))
            {
                ImUtf8.SetClipboardText(presetString);
            }
        }
        else if (
            _plugin.HasPendingAdventurerPlateCopy
            && !_plugin.IsCapturingAdventurerPlate
            && _plugin.PlateStolenInfo == null
            && !string.IsNullOrEmpty(_plugin.PlateErrorMessage)
        )
        {
            _plugin.ClearPendingAdventurerPlateCopy();
        }

        bool isCapturing = _plugin.IsCapturingAdventurerPlate;
        var captureIcon = isCapturing ? FontAwesomeIcon.Spinner : FontAwesomeIcon.Camera;
        var captureTooltip = string.IsNullOrEmpty(_plugin.PlateErrorMessage)
            ? isCapturing ? "Capturing Portrait..." : "Capture Portrait"
            : isCapturing ? "Capturing Portrait..." : $"Capture Portrait\nLast error: {_plugin.PlateErrorMessage}";
        DrawIconButton(
            captureIcon,
            "PlateOverlayCapture",
            captureTooltip,
            () => _plugin.StealAdventurerPlateData(),
            disabled: isCapturing
        );

        ImGui.SameLine();
        var hasCapturedPlate = _plugin.PlateStolenInfo != null;
        var copyTooltip = isCapturing
            ? _plugin.HasPendingAdventurerPlateCopy
                ? "Will copy after capture completes."
                : "Copy after capture completes."
            : hasCapturedPlate
                ? "Copy PortraitHelper String"
                : "Capture current Portrait and copy PortraitHelper String";
        DrawIconButton(
            FontAwesomeIcon.Copy,
            "PlateOverlayCopy",
            copyTooltip,
            () =>
            {
                if (_plugin.IsCapturingAdventurerPlate)
                {
                    _plugin.RequestAdventurerPlateCopyAfterCapture();
                    return;
                }

                if (_plugin.PlateStolenInfo != null)
                {
                    var presetString = _plugin.GeneratePresetString(_plugin.PlateStolenInfo.Value);
                    if (!string.IsNullOrEmpty(presetString))
                    {
                        ImUtf8.SetClipboardText(presetString);
                    }
                    return;
                }

                _plugin.RequestAdventurerPlateCopyAfterCapture();
                _plugin.StealAdventurerPlateData();
            }
        );

        DrawOverlaySeparator();
        ImGui.SameLine();
        DrawIconButton(
            FontAwesomeIcon.TrashAlt,
            "PlateOverlayClearCache",
            "Clear Portrait Cache",
            () => _plugin.ClearAdventurerPlateCache()
        );

        ImGui.SameLine();
        DrawIconButton(
            FontAwesomeIcon.WindowMaximize,
            "PlateOverlayToggle",
            "Toggle PortraitStealer Window",
            () => _plugin.ToggleMainUI()
        );
    }

    private unsafe Vector2? SnapToCharaCard()
    {
        if (!TryGetCharaCardAddon(out var addon) || !addon.IsVisible)
            return null;

        var addonStruct = (AtkUnitBase*)addon.Address;
        if (addonStruct == null)
            return null;

        var rootNode = addonStruct->RootNode;
        if (rootNode == null)
            return null;

        var device = Device.Instance();
        if (device == null)
            return null;

        var top = rootNode->GetYFloat();
        var left = rootNode->GetXFloat();
        var plateHeight = rootNode->GetHeight() * rootNode->GetScaleY();
        var screenHeight = device->Height;
        var windowHeight = ImGui.GetWindowSize().Y;
        var margin = 4f;

        var fitsBelow = top + plateHeight + windowHeight + margin <= screenHeight;
        var targetY = fitsBelow ? top + plateHeight + margin : top - windowHeight - margin;

        return new Vector2(left + margin, targetY);
    }

    private static bool DrawIconButton(FontAwesomeIcon icon, string id, string tooltip, Action onClick, bool disabled = false)
    {
        var key = id.StartsWith("##", StringComparison.Ordinal) ? id : $"##{id}";
        bool clicked = false;
        using (ImRaii.Disabled(disabled))
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            clicked = ImGui.Button(icon.ToIconString() + key);
        }

        if (!string.IsNullOrEmpty(tooltip))
        {
            var hoverFlags = disabled ? ImGuiHoveredFlags.AllowWhenDisabled : ImGuiHoveredFlags.None;
            if (ImGui.IsItemHovered(hoverFlags))
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(tooltip);
                ImGui.EndTooltip();
            }
        }

        if (clicked && !disabled)
        {
            onClick?.Invoke();
        }

        return clicked && !disabled;
    }

    private static void DrawOverlaySeparator()
    {
        ImGui.SameLine();

        var height = ImGui.GetFrameHeight();
        var pos = ImGui.GetWindowPos() + ImGui.GetCursorPos();

        ImGui.GetWindowDrawList().AddLine(
            pos + new Vector2(3f, 0f),
            pos + new Vector2(3f, height),
            ImGui.GetColorU32(ImGuiCol.Separator)
        );

        ImGui.Dummy(new Vector2(7f, height));
    }

    private static bool TryGetCharaCardAddon(out AtkUnitBasePtr addon)
    {
        addon = default;
        try
        {
            addon = Plugin.GameGui.GetAddonByName("CharaCard", 1);
            if (addon.IsNull)
                return false;

            return true;
        }
        catch
        {
            addon = default;
            return false;
        }
    }
}
