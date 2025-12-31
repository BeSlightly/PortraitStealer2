using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;
using PortraitStealer.Services;

namespace PortraitStealer.Windows;

public partial class MainWindow
{
    private void DrawTabs()
    {
        using (var tab = ImUtf8.TabItem("Adventurer Plate"))
        {
            if (tab)
            {
                ImGuiHelpers.ScaledDummy(MediumSpacing);
                DrawAdventurerPlateTabContent();
                ImGuiHelpers.ScaledDummy(MediumSpacing);
            }
        }
        using (var tab = ImUtf8.TabItem("Duty Portraits"))
        {
            if (tab)
            {
                ImGuiHelpers.ScaledDummy(MediumSpacing);
                DrawCachedDutyPortraitsTabContent();
                ImGuiHelpers.ScaledDummy(MediumSpacing);
            }
        }
    }

    private void DrawAdventurerPlateTabContent()
    {
        DrawInstructions(
            _isAgentCharaCardValid
                ? "The Adventurer Plate window is open and ready. Last 10 captured plates are shown below."
                : "Open an Adventurer Plate to capture it's portrait.\nLast 10 captured plates are shown below."
        );
        bool captureButtonHovered = false;
        bool isCapturing = _plugin.IsCapturingAdventurerPlate;
        using (var disabled = ImRaii.Disabled(!_isAgentCharaCardValid || isCapturing))
        {
            var captureIcon = isCapturing ? FontAwesomeIcon.Spinner : FontAwesomeIcon.Camera;
            var captureText = isCapturing ? "Capturing..." : "Capture Portrait";
            
            if (
                DrawIconButton(
                    captureIcon,
                    $"{captureText}##PlateCapture",
                    Vector2.Zero,
                    () =>
                    {
                        _plugin.StealAdventurerPlateData();
                        ClearCopyStatus();
                        SetSelectedPlate(null);
                    }
                )
            ) { }
            captureButtonHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        }

        ImGui.SameLine();

        if (
            DrawIconButton(
                FontAwesomeIcon.TrashAlt,
                "Clear Cache##ClearAdventurerPlateCache",
                Vector2.Zero,
                () =>
                {
                    _plugin.ClearAdventurerPlateCache();
                    ClearCopyStatus();
                    SetSelectedPlate(null);
                }
            )
        ) { }

        ImGui.SameLine(0, MediumSpacing * ImUtf8.GlobalScale);
        var showOverlayButton = _plugin.Configuration.ShowCaptureButtonOnAdventurerPlate;
        if (ImGui.Checkbox("Show capture button on plate", ref showOverlayButton))
        {
            _plugin.Configuration.ShowCaptureButtonOnAdventurerPlate = showOverlayButton;
            _plugin.Configuration.Save();
        }

        if (captureButtonHovered)
        {
            if (isCapturing)
            {
                ImUtf8.HoverTooltip("Capture in progress... Please wait."u8);
            }
            else if (!_isAgentCharaCardValid)
            {
                ImUtf8.HoverTooltip("Please open your Adventurer Plate first."u8);
            }
        }

        DrawSectionSeparator("Adventurer Plates");

        var cachedPlates = _plugin.AdventurerPlateCacheService?.GetCachedPortraits() ?? new List<CachedAdventurerPlateInfo>();
        float listWidth = 280 * ImUtf8.GlobalScale;
        float listHeight = ImGui.GetContentRegionAvail().Y - DefaultSpacing;

        using (
            var listContainerChild = ImUtf8.Child(
                "PlateListContainer",
                new Vector2(listWidth, listHeight),
                false
            )
        )
        {
            if (listContainerChild)
            {
                DrawAdventurerPlateListPane(cachedPlates, listWidth);
            }
        }

        ImGui.SameLine(0, MediumSpacing);

        using (
            var detailChild = ImUtf8.Child(
                "PlateDetailPane",
                new Vector2(0, listHeight),
                false,
                ImGuiWindowFlags.None
            )
        )
        {
            if (detailChild)
            {
                DrawAdventurerPlateDetailPane(ImGui.GetContentRegionAvail().Y, cachedPlates);
            }
        }
    }

    private void DrawAdventurerPlateListPane(List<CachedAdventurerPlateInfo> cachedPlates, float listWidth)
    {
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 5f);
        using var color = ImRaii.PushColor(
            ImGuiCol.ChildBg,
            ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBg]
        );
        using var listContentChild = ImUtf8.Child(
            "PlateListContent",
            Vector2.Zero,
            true,
            ImGuiWindowFlags.HorizontalScrollbar
        );

        if (!listContentChild)
            return;

        if (_plugin.IsCapturingAdventurerPlate)
        {
            using var textColor = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
            ImUtf8.Spinner("##CaptureSpinner"u8, 8f, 2, ImGui.GetColorU32(ImGuiColors.DalamudYellow));
            ImGui.SameLine();
            ImUtf8.TextWrapped("Capturing adventurer plate portrait...");
            ImGui.Separator();
        }
        else if (!string.IsNullOrEmpty(_plugin.PlateErrorMessage))
        {
            using var textColor = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
            using (var font = ImRaii.PushFont(UiBuilder.IconFont))
            {
                ImUtf8.Text(FontAwesomeIcon.ExclamationTriangle.ToIconString());
            }
            ImGui.SameLine();
            ImUtf8.TextWrapped($"Capture Error: {_plugin.PlateErrorMessage}");
            ImGui.Separator();
        }

        if (_plugin.PlateStolenInfo != null)
        {
            var currentInfo = _plugin.PlateStolenInfo.Value;
            var playerName = currentInfo.PlayerName ?? "Unknown Player";

            bool alreadyInCache = cachedPlates.Any(p => p.PlayerName == playerName && 
                Math.Abs((p.Timestamp - currentInfo.Timestamp).TotalSeconds) < 5);

            if (!alreadyInCache)
            {
                using var textColor = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.ParsedBlue);
                using (var font = ImRaii.PushFont(UiBuilder.IconFont))
                {
                    ImUtf8.Text(FontAwesomeIcon.Star.ToIconString());
                }
                ImGui.SameLine();
                
                var label = $" {playerName} ({currentInfo.ClassJobAbbreviation}) [Just Captured]";
                bool isSelected = GetSelectedPlatePlayerName(cachedPlates) == null; // Select if no cached plate selected
                
                if (ImUtf8.Selectable(label, isSelected))
                {
                    SetSelectedPlate(null); // Clear selection to show just captured
                    ClearCopyStatus();
                }
                
                if (ImGui.IsItemHovered())
                    ImUtf8.HoverTooltip("Just captured - will be added to cache list");
                
                ImGui.Separator();
            }
        }

        if (!cachedPlates.Any())
        {
            if (_plugin.PlateStolenInfo == null)
            {
                var availableRegion = ImGui.GetContentRegionAvail();
                if (availableRegion.X <= 0 || availableRegion.Y <= 0)
                    return;

                using var textColor = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
                var message = "No cached adventurer plates.\nCapture some plates to populate.";
                var wrapWidth = availableRegion.X - ImGui.GetStyle().WindowPadding.X * 2;
                var textSize = ImGui.CalcTextSize(message, true, wrapWidth);

                float startY = ImGui.GetCursorPosY() + Math.Max(0, (availableRegion.Y - textSize.Y) * 0.5f);
                float startX = ImGui.GetCursorPosX() + Math.Max(0, (availableRegion.X - textSize.X) * 0.5f);

                ImGui.SetCursorPos(new Vector2(startX, startY));
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrapWidth);
                ImUtf8.TextWrapped(message);
                ImGui.PopTextWrapPos();
            }
            return;
        }

        var iconFont = UiBuilder.IconFont;
        string? selectedPlayerName = GetSelectedPlatePlayerName(cachedPlates);

        foreach (var entry in cachedPlates)
        {
            var indicatorIcon = FontAwesomeIcon.CheckCircle;
            var indicatorColor = ImGuiColors.ParsedGreen;
            var indicatorTooltip = "Cached adventurer plate";
            
            var label = $" {entry.PlayerName} ({entry.ClassJobAbbreviation})";
            bool isSelected = entry.PlayerName == selectedPlayerName;

            using var id = ImRaii.PushId(entry.PlayerName);

            using (var font = ImRaii.PushFont(iconFont))
            using (var c = ImRaii.PushColor(ImGuiCol.Text, indicatorColor))
            {
                ImUtf8.Text(indicatorIcon.ToIconString());
            }

            ImGui.SameLine();

            if (ImUtf8.Selectable(label, isSelected))
            {
                if (selectedPlayerName != entry.PlayerName)
                {
                    SetSelectedPlate(entry.PlayerName);
                    ClearCopyStatus();
                }
                else
                {
                    SetSelectedPlate(null);
                    ClearCopyStatus();
                }
            }

            if (ImGui.IsItemHovered())
                ImUtf8.HoverTooltip($"{indicatorTooltip}\nCaptured: {entry.Timestamp:G}");

            if (isSelected)
                ImGui.SetItemDefaultFocus();
        }
    }

    private void DrawAdventurerPlateDetailPane(float availablePaneHeight, List<CachedAdventurerPlateInfo> cachedPlates)
    {
        var selectedPlate = GetSelectedPlateInfo(cachedPlates);
        var selectedPlayerName = GetSelectedPlatePlayerName(cachedPlates);

        if (selectedPlate == null && selectedPlayerName == null && _plugin.PlateStolenInfo != null)
        {
            var info = _plugin.PlateStolenInfo.Value;
            var playerName = info.PlayerName ?? "Unknown Player";
            var cachedData = new CachedPortraitData(playerName, 0, 0, info.ClassJobId, info.ClassJobAbbreviation, info.Timestamp)
                .WithFullData(info, info.ImagePath);

            DrawPortraitDetailPane(
                $"Just Captured: {playerName} ({info.ClassJobAbbreviation})",
                $"Class/Job: {info.ClassJobAbbreviation} | Captured: {info.Timestamp:G}",
                "PortraitImageFrame_JustCaptured",
                "ActionGroupChild_JustCaptured",
                cachedData,
                info,
                "JustCaptured",
                availablePaneHeight
            );
            return;
        }

        if (selectedPlate != null)
        {
            var plateInfo = selectedPlate.Value;
            var cachedData = new CachedPortraitData(
                plateInfo.PlayerName,
                0,
                0,
                plateInfo.ClassJobId,
                plateInfo.ClassJobAbbreviation,
                plateInfo.Timestamp
            ).WithFullData(plateInfo.PortraitInfo, plateInfo.ImagePath);

            DrawPortraitDetailPane(
                $"Cached: {plateInfo.PlayerName} ({plateInfo.ClassJobAbbreviation})",
                $"Class/Job: {plateInfo.ClassJobAbbreviation} | Captured: {plateInfo.Timestamp:G}",
                $"CachedPlateImageFrame_{plateInfo.PlayerName}",
                $"CachedPlateActionGroup_{plateInfo.PlayerName}",
                cachedData,
                plateInfo.PortraitInfo,
                $"CachedPlate_{plateInfo.PlayerName}",
                availablePaneHeight
            );
            return;
        }

        DrawInitialMessage(
            cachedPlates.Any() 
                ? "Select an adventurer plate from the list to view details."
                : _isAgentCharaCardValid
                    ? "Click 'Capture Portrait' above to begin."
                    : "Open your Adventurer Plate first, then capture to populate the list.",
            availablePaneHeight
        );
    }

    private void DrawPortraitDetailPane(
        string title,
        string subtitle,
        string imageFrameId,
        string actionGroupId,
        CachedPortraitData cachedData,
        StolenPortraitInfo portraitInfo,
        string actionGroupContext,
        float availablePaneHeight)
    {
        ImUtf8.Text(title);
        using (var color = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
        {
            ImUtf8.Text(subtitle);
        }
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(DefaultSpacing);

        float availableHeightForImage =
            availablePaneHeight
            - ImGui.GetCursorPosY()
            - _actionGroupHeightEstimate
            - MediumSpacing;

        Vector2 imageAvailableSize = new Vector2(
            ImGui.GetContentRegionAvail().X,
            Math.Max(250 * ImUtf8.GlobalScale, availableHeightForImage)
        );
        imageAvailableSize.Y = Math.Max(0, imageAvailableSize.Y);

        using (var color = ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.0f, 0.0f, 0.0f, 0.3f)))
        using (var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 5f))
        using (
            var imageFrame = ImUtf8.Child(
                imageFrameId,
                imageAvailableSize,
                false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
            )
        )
        {
            if (imageFrame)
            {
                _cachedPortraitDisplay.SetData(cachedData);
                _cachedPortraitDisplay.Draw(ImGui.GetContentRegionAvail());
            }
        }

        ImGuiHelpers.ScaledDummy(MediumSpacing);

        using (
            var actionChild = ImUtf8.Child(
                actionGroupId,
                new Vector2(0, 0),
                false,
                ImGuiWindowFlags.AlwaysAutoResize
                    | ImGuiWindowFlags.NoScrollbar
                    | ImGuiWindowFlags.NoScrollWithMouse
            )
        )
        {
            if (actionChild)
            {
                DrawActionGroup(portraitInfo, actionGroupContext);
            }
        }
    }

    // --- Cached Duty Portraits Tab ---
    private void DrawCachedDutyPortraitsTabContent()
    {
        DrawInstructions(
            "Caches party member portraits when the 'Party Members' UI is open or updated.\nSelect an entry from the list below to view the portrait and copy its data."
        );

        // Use DrawIconButton again
        if (
            DrawIconButton( // Uses instance helper
                FontAwesomeIcon.TrashAlt,
                "Clear Cache##ClearCacheBtn",
                Vector2.Zero,
                () =>
                {
                    _plugin.ClearDutyCache();
                    _selectedCacheObjectId = 0;
                    _cachedPortraitDisplay.SetData(null);
                    ClearCopyStatus();
                    _cacheFilterDirty = true;
                }
            )
        ) { }

        DrawSectionSeparator("Cached Portraits");

        // Use the cached _populatedCache
        float listWidth = 280 * ImUtf8.GlobalScale;
        float listHeight = ImGui.GetContentRegionAvail().Y - DefaultSpacing;

        // --- Left Pane: Filtered List Container ---
        using (
            var listContainerChild = ImUtf8.Child(
                "FilteredListPaneContainer",
                new Vector2(listWidth, listHeight),
                false
            )
        )
        {
            if (listContainerChild)
            {
                DrawListPane(_populatedCache, listWidth);
            }
        }

        ImGui.SameLine(0, MediumSpacing);

        // --- Right Pane: Details Container ---
        using (
            var detailChild = ImUtf8.Child(
                "DetailPane",
                new Vector2(0, listHeight),
                false,
                ImGuiWindowFlags.None
            )
        ) // Allow scrolling
        {
            if (detailChild)
            {
                DrawDetailPane(ImGui.GetContentRegionAvail().Y);
            }
        }
    }

    private void DrawListPane(List<CachedPortraitData> populatedCache, float listWidth)
    {
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 5f);
        using var color = ImRaii.PushColor(
            ImGuiCol.ChildBg,
            ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBg]
        );
        using var listContentChild = ImUtf8.Child(
            "ListContent",
            Vector2.Zero,
            true,
            ImGuiWindowFlags.HorizontalScrollbar
        );

        if (!listContentChild)
            return;

        if (!populatedCache.Any())
        {
            // --- START REPLACEMENT V4 ---
            var availableRegion = ImGui.GetContentRegionAvail();
            // Ensure we have some space to draw in
            if (availableRegion.X <= 0 || availableRegion.Y <= 0)
                return;

            using var textColor = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);

            // Prepare content
            var message = "Cache is empty.\nOpen the Party Members UI\nin a duty to populate.";

            // Calculate text size using the actual available width for wrapping
            var wrapWidth = availableRegion.X - ImGui.GetStyle().WindowPadding.X * 2; // Account for padding
            var textSize = ImGui.CalcTextSize(message, true, wrapWidth);

            // Calculate starting position for vertical centering
            float startY =
                ImGui.GetCursorPosY()
                + Math.Max(0, (availableRegion.Y - textSize.Y) * 0.5f);

            // Calculate starting X for horizontal centering
            float startX =
                ImGui.GetCursorPosX() + Math.Max(0, (availableRegion.X - textSize.X) * 0.5f);

            // Draw Text (Centered horizontally within the block)
            ImGui.SetCursorPos(new Vector2(startX, startY));
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrapWidth); // Use calculated wrap width
            ImUtf8.TextWrapped(message);
            ImGui.PopTextWrapPos();

            return; // Don't draw anything else in the list
            // --- END REPLACEMENT V4 ---
        }

        var iconFont = UiBuilder.IconFont; // Cache outside loop

        foreach (var entry in populatedCache)
        {
            var (indicatorIcon, indicatorColor, indicatorTooltip) = GetCacheEntryStatus(entry); // Uses helper
            var label =
                $" Slot {entry.SlotIndex + 1}: {entry.PlayerName} ({entry.ClassJobAbbreviation})";
            bool isSelected = entry.ClientObjectId == _selectedCacheObjectId;

            using var id = ImRaii.PushId((int)entry.ClientObjectId); // Use int ID

            // Draw Icon with minimal overhead
            using (var font = ImRaii.PushFont(iconFont))
            using (
                var c =
                    indicatorColor != default
                        ? ImRaii.PushColor(ImGuiCol.Text, indicatorColor)
                        : null
            )
            {
                ImUtf8.Text(indicatorIcon.ToIconString());
            }

            ImGui.SameLine();

            if (ImUtf8.Selectable(label, isSelected))
            {
                if (_selectedCacheObjectId != entry.ClientObjectId)
                {
                    _selectedCacheObjectId = entry.ClientObjectId;
                    _cachedPortraitDisplay.SetData(entry);
                    ClearCopyStatus();
                }
                else
                {
                    _selectedCacheObjectId = 0;
                    _cachedPortraitDisplay.SetData(null);
                    ClearCopyStatus();
                }
            }

            if (ImGui.IsItemHovered())
                ImUtf8.HoverTooltip(indicatorTooltip);

            if (isSelected)
                ImGui.SetItemDefaultFocus();
        }
    }

    private void DrawDetailPane(float availablePaneHeight)
    {
        var selectedData = _selectedCacheInfoForDisplay;

        if (selectedData == null)
        {
            DrawInitialMessage("Select a cached portrait from the list.", availablePaneHeight);
            return;
        }

        var selectedInfo = selectedData.Value;

        if (selectedInfo.FullPortraitData.HasValue)
        {
            DrawPortraitDetailPane(
                $"Selected: Slot {selectedInfo.SlotIndex + 1} - {selectedInfo.PlayerName} ({selectedInfo.ClassJobAbbreviation})",
                $"Object ID: {selectedInfo.ClientObjectId:X} | Last Update: {selectedInfo.Timestamp:G}",
                $"PortraitImageFrame_{selectedInfo.ClientObjectId}",
                $"ActionGroupChild_{selectedInfo.ClientObjectId}",
                selectedInfo,
                selectedInfo.FullPortraitData.Value,
                $"Cache_{selectedInfo.ClientObjectId}",
                availablePaneHeight
            );
        }
        else
        {
            // Show status message for entries without full data
            ImUtf8.Text(
                $"Selected: Slot {selectedInfo.SlotIndex + 1} - {selectedInfo.PlayerName} ({selectedInfo.ClassJobAbbreviation})"
            );
            using (var color = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImUtf8.Text(
                    $"Object ID: {selectedInfo.ClientObjectId:X} | Last Update: {selectedInfo.Timestamp:G}"
                );
            }
            ImGui.Separator();
            
            var (icon, msgColor, tooltip) = GetCacheEntryStatus(selectedInfo);
            using var statusColor = ImRaii.PushColor(ImGuiCol.Text, msgColor);
            DrawInitialMessage($"{icon.ToIconString()} {tooltip.Split('\n')[0]}", availablePaneHeight);
        }
    }
}
