using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using static FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentBannerInterface;

namespace PortraitStealer.Services;

public unsafe partial class DutySlotCacheService
{
    private void OnFrameworkUpdate(IFramework framework)
    {
        Storage* storage = null;
        bool isAgentValidNow = false;
        try
        {
            var agentInterface = _gameGui.GetAgentById((int)AgentId.BannerParty);
            if (!agentInterface.IsNull && agentInterface.IsAgentActive)
            {
                var agent = (AgentBannerInterface*)agentInterface.Address;
                if (agent->Data != null)
                {
                    storage = agent->Data;
                    isAgentValidNow = true;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Exception checking agent validity in framework update.");
            isAgentValidNow = false;
        }

        DateTime currentTime = DateTime.MinValue;

        if (isAgentValidNow && storage != null)
        {
            currentTime = DateTime.Now;
            for (int i = 0; i < 8; i++)
            {
                _cacheLock.EnterWriteLock();
                try
                {
                    ProcessSlotBasicCheckAndQueue(i, storage, currentTime);
                }
                finally
                {
                    _cacheLock.ExitWriteLock();
                }
            }
        }

        }

    private void ProcessSlotBasicCheckAndQueue(int index, Storage* storage, DateTime currentTime)
    {
        try
        {
            ref var characterData = ref storage->Characters[index];
            var objectId = characterData.CharaView.ClientObjectId;
            byte classJobId = characterData.CharaView.PortraitCharacterData.ClassJobId;
            bool isPopulated = objectId != 0 && objectId != 0xE0000000 && classJobId > 0;

            var currentCacheEntry = _cache[index];

            if (isPopulated)
            {
                bool needsBasicUpdate =
                    currentCacheEntry == null
                    || currentCacheEntry.Value.ClientObjectId != objectId
                    || currentCacheEntry.Value.ClassJobId != classJobId;
                bool justUpdatedBasic = false;

                if (needsBasicUpdate)
                {
                    FileHelpers.SafeDeleteFile(
                        currentCacheEntry?.TemporaryImagePath,
                        _log,
                        $"Basic Update Slot {index + 1}"
                    );
                    string name = characterData.Name1.ToString();
                    name = string.IsNullOrEmpty(name) ? "???" : name;

                    string jobAbbr = _portraitDataService.GetClassJobAbbreviation(classJobId);
                    var newBasicEntry = new CachedPortraitData(
                        name,
                        objectId,
                        index,
                        classJobId,
                        jobAbbr,
                        currentTime
                    );
                    _cache[index] = newBasicEntry;
                    justUpdatedBasic = true;
                    currentCacheEntry = newBasicEntry;
                }

                if (!justUpdatedBasic && currentCacheEntry.HasValue && currentCacheEntry.Value.NeedsFullDataFetchAttempt)
                {
                    if (currentTime - currentCacheEntry.Value.LastBasicUpdateTimestamp > FetchDelay)
                    {
                        if (_pendingFullDataQueue.Count < 16)
                        {
                            _pendingFullDataQueue.Enqueue(index);
                            _cache[index] = currentCacheEntry.Value.WithFetchQueued();
                        }
                    }
                }
            }
            else
            {
                if (currentCacheEntry != null)
                {
                    FileHelpers.SafeDeleteFile(
                        currentCacheEntry?.TemporaryImagePath,
                        _log,
                        $"Slot Empty {index + 1}"
                    );
                    _cache[index] = null;
                }
            }
        }
        catch (AccessViolationException avEx)
        {
            _log.Warning(
                avEx,
                $"Access Violation during basic check/queue for slot {index + 1}. Clearing cache entry."
            );
            FileHelpers.SafeDeleteFile(
                _cache[index]?.TemporaryImagePath,
                _log,
                $"AV Exception Slot {index + 1}"
            );
            _cache[index] = null;
        }
        catch (Exception ex)
        {
            _log.Error(
                ex,
                $"Unexpected error during basic check/queue for slot {index + 1}. Clearing cache entry."
            );
            FileHelpers.SafeDeleteFile(_cache[index]?.TemporaryImagePath, _log, $"Exception Slot {index + 1}");
            _cache[index] = null;
        }
    }
}
