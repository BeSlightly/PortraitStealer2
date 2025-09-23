using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using static FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentBannerInterface;

namespace PortraitStealer.Services;

public unsafe class PortraitDataService
{
    private readonly IDataManager _dataManager;
    private readonly IPluginLog _log;
    private readonly IGameGui _gameGui;
    private readonly IClientState _clientState;

    private readonly ConcurrentDictionary<byte, string> _classJobAbbrCache = new();
    private readonly Lumina.Excel.ExcelSheet<ClassJob>? _classJobSheet;

    public PortraitDataService(IDataManager dataManager, IPluginLog log, IGameGui gameGui, IClientState clientState)
    {
        _dataManager = dataManager;
        _log = log;
        _gameGui = gameGui;
        _clientState = clientState;
        try
        {
            _classJobSheet = _dataManager.GetExcelSheet<ClassJob>();
            if (_classJobSheet == null)
            {
                _log.Warning(
                    "ClassJob ExcelSheet not found during PortraitDataService initialization."
                );
            }
        }
        catch (Exception ex)
        {
            _log.Error(
                ex,
                "Failed to load ClassJob sheet during PortraitDataService initialization."
            );
        }
    }

    internal StolenPortraitInfo? TryGetAdventurerPlateData(out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            var agentInterface = _gameGui.GetAgentById((int)AgentId.CharaCard);
            if (agentInterface.IsNull || !agentInterface.IsAgentActive)
            {
                errorMessage = "Adventurer Plate is not open.";
                return null;
            }
            var agentCharaCard = (AgentCharaCard*)agentInterface.Address;
            if (agentCharaCard->Data == null)
            {
                errorMessage = "Adventurer Plate could not be found.";
                _log.Warning("TryGetAdventurerPlateData: AgentCharaCard Data is null");
                return null;
            }

            var storage = agentCharaCard->Data;
            var portraitTexture = storage->PortraitTexture;
            if (portraitTexture == null)
            {
                errorMessage = "Portrait texture not found on Adventurer Plate.";
                _log.Warning("TryGetAdventurerPlateData: PortraitTexture is null in storage");
                return null;
            }
            var finalPortraitTexture = portraitTexture;

            var bannerFrame = storage->BannerFrame;
            var bannerDecoration = storage->BannerDecoration;
            byte portraitClassJobId = storage->CharaView.PortraitCharacterData.ClassJobId;
            string jobAbbr = GetClassJobAbbreviation(portraitClassJobId);

            string? playerName = null;
            bool isOwnPlate = false;
            try
            {
                var nameFromStorage = storage->Name.ToString();
                if (!string.IsNullOrEmpty(nameFromStorage))
                {
                    playerName = nameFromStorage;
                    _log.Debug($"Extracted player name from adventurer plate storage: {playerName}");

                    if (_clientState.LocalPlayer != null)
                    {
                        var localPlayerName = _clientState.LocalPlayer.Name.TextValue;
                        isOwnPlate = string.Equals(playerName, localPlayerName, StringComparison.OrdinalIgnoreCase);
                        _log.Debug($"Is own plate: {isOwnPlate} (local: {localPlayerName}, plate: {playerName})");
                    }
                }
                else
                {
                    if (_clientState.LocalPlayer != null)
                    {
                        playerName = _clientState.LocalPlayer.Name.TextValue;
                        isOwnPlate = true;
                        _log.Debug($"Using local player name as fallback: {playerName}");
                    }
                }
                
                if (string.IsNullOrEmpty(playerName))
                {
                    playerName = "Unknown Player";
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to extract player name from adventurer plate, using fallback");
                playerName = "Unknown Player";
            }

            if (!isOwnPlate)
            {
                _log.Warning($"Capturing adventurer plate for {playerName} - preset data may not be compatible with your character");
            }

            _log.Debug($"Adventurer plate data - Player: {playerName}, Job: {jobAbbr}, " +
                      $"ContentId: {storage->ContentId:X}, EntityId: {storage->EntityId:X}");

            ExportedPortraitData exportedData;
            var exportedDataPtr = &exportedData;
            storage->CharaView.ExportPortraitData(exportedDataPtr);

            return new StolenPortraitInfo(
                exportedData,
                bannerFrame,
                bannerDecoration,
                portraitClassJobId,
                jobAbbr,
                finalPortraitTexture,
                playerName
            );
        }
        catch (Exception ex)
        {
            errorMessage = $"Error reading Adv. Plate data: {ex.Message}";
            _log.Error(ex, "TryGetAdventurerPlateData: Failed to read Adventurer Plate data");
            return null;
        }
    }

    internal StolenPortraitInfo? TryGetDutyPortraitData(
        int partySlotIndex,
        out string? errorMessage
    )
    {
        errorMessage = null;
        if (partySlotIndex < 0 || partySlotIndex > 7)
        {
            errorMessage = $"Invalid party slot index: {partySlotIndex}. Must be 0-7.";
            return null;
        }

        try
        {
            var agentInterface = _gameGui.GetAgentById((int)AgentId.BannerParty);
            if (agentInterface.IsNull || !agentInterface.IsAgentActive)
            {
                errorMessage = "Duty/Party list portrait UI not detected.";
                return null;
            }
            var agentBannerInterface = (AgentBannerInterface*)agentInterface.Address;
            Storage* storage = agentBannerInterface->Data;
            if (storage == null)
            {
                errorMessage = "Party Members UI could not be found.";
                return null;
            }

            ref AgentBannerInterface.Storage.CharacterData characterData = ref storage->Characters[
                partySlotIndex
            ];
            ExportedPortraitData exportedData;

            fixed (CharaViewPortrait* targetCharaViewPortrait = &characterData.CharaView)
            {
                try
                {
                    targetCharaViewPortrait->ExportPortraitData(&exportedData);
                }
                catch (AccessViolationException ex)
                {
                    errorMessage =
                        $"Internal Error: Memory access violation calling ExportPortraitData for slot {partySlotIndex + 1}.";
                    _log.Error(ex, errorMessage);
                    return null;
                }
                catch (Exception ex)
                {
                    errorMessage =
                        $"Internal Error: Exception calling ExportPortraitData for slot {partySlotIndex + 1}: {ex.Message}";
                    _log.Error(ex, errorMessage);
                    return null;
                }

                byte portraitClassJobId = targetCharaViewPortrait->PortraitCharacterData.ClassJobId;
                if (portraitClassJobId == 0)
                {
                    errorMessage =
                        $"Party slot {partySlotIndex + 1} appears to be empty or has invalid data (ClassJob ID is 0).";
                    return null;
                }
                string jobAbbr = GetClassJobAbbreviation(portraitClassJobId);
                ushort bannerFrame = 0;
                ushort bannerDecoration = 0;

                fixed (AgentBannerInterface.Storage.CharacterData* characterDataPtr = &characterData)
                {
                    byte* baseAddress = (byte*)characterDataPtr;
                    bannerFrame = *(ushort*)(baseAddress + MemoryOffsets.BannerCharacterData.BannerFrame);
                    bannerDecoration = *(ushort*)(baseAddress + MemoryOffsets.BannerCharacterData.BannerDecoration);
                }

                return new StolenPortraitInfo(
                    exportedData,
                    bannerFrame,
                    bannerDecoration,
                    portraitClassJobId,
                    jobAbbr
                );
            }
        }
        catch (Exception ex)
        {
            errorMessage =
                $"Unexpected error accessing duty data for slot {partySlotIndex + 1}: {ex.Message}";
            _log.Error(
                ex,
                $"Unexpected error in TryGetDutyPortraitData for slot {partySlotIndex + 1}"
            );
            return null;
        }
    }

    

    public string GetClassJobAbbreviation(byte classJobId)
    {
        if (_classJobAbbrCache.TryGetValue(classJobId, out var cachedAbbr))
        {
            return cachedAbbr;
        }

        string fallbackAbbr = "?";
        if (_classJobSheet == null)
        {
            return fallbackAbbr;
        }

        try
        {
            // Corrected: Use TryGetRow
            if (_classJobSheet.TryGetRow(classJobId, out var jobRow))
            {
                // jobRow is guaranteed to be non-null here if TryGetRow returns true for struct types that Lumina handles this way
                string abbrString = jobRow.Abbreviation.ToString();
                if (!string.IsNullOrEmpty(abbrString))
                {
                    _classJobAbbrCache[classJobId] = abbrString;
                    return abbrString;
                }
            }
            _classJobAbbrCache[classJobId] = fallbackAbbr;
            return fallbackAbbr;
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"Failed to get ClassJob abbreviation for ID {classJobId}");
            _classJobAbbrCache[classJobId] = fallbackAbbr;
            return fallbackAbbr;
        }
    }
}
