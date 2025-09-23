using System;
using System.IO;
using System.Threading;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using static FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentBannerInterface;

namespace PortraitStealer.Services;

public unsafe partial class DutySlotCacheService
{
    private readonly SemaphoreSlim _imageSaveSemaphore = new SemaphoreSlim(2, 2);

    public void ProcessPendingFullDataRequests()
    {
        int processedCount = 0;
        const int maxPerFrame = 1;
        AgentBannerInterface* agentBannerInterface = null;
        Storage* currentStorage = null;
        bool isAgentValidNow = false;

        try
        {
            var agentInterface = _gameGui.GetAgentById((int)AgentId.BannerParty);
            if (!agentInterface.IsNull && agentInterface.IsAgentActive)
            {
                agentBannerInterface = (AgentBannerInterface*)agentInterface.Address;
                if (agentBannerInterface->Data != null)
                {
                    currentStorage = agentBannerInterface->Data;
                    isAgentValidNow = true;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Exception checking agent validity on UI thread.");
            isAgentValidNow = false;
            currentStorage = null;
        }

        if (!isAgentValidNow)
        {
            if (!_pendingFullDataQueue.IsEmpty)
            {
                while (_pendingFullDataQueue.TryDequeue(out _)) { }
            }
            return;
        }

        while (processedCount < maxPerFrame && _pendingFullDataQueue.TryDequeue(out int slotIndex))
        {
            processedCount++;
            if (slotIndex < 0 || slotIndex >= 8)
                continue;

            uint currentObjectIdInSlot = 0;
            bool slotStillPopulatedAndValid = false;
            try
            {
                if (currentStorage != null)
                {
                    ref var characterDataNow = ref currentStorage->Characters[slotIndex];
                    currentObjectIdInSlot = characterDataNow.CharaView.ClientObjectId;
                    byte classJobIdNow = characterDataNow
                        .CharaView
                        .PortraitCharacterData
                        .ClassJobId;
                    slotStillPopulatedAndValid =
                        currentObjectIdInSlot != 0
                        && currentObjectIdInSlot != 0xE0000000
                        && classJobIdNow > 0;
                }
            }
            catch (Exception ex)
            {
                _log.Error(
                    ex,
                    $"Exception re-validating slot {slotIndex + 1} context on UI thread."
                );
                slotStillPopulatedAndValid = false;
                currentObjectIdInSlot = 0;
            }

            StolenPortraitInfo? fetchedData = null;
            string? fetchErrorMessage = null;
            bool performFetch = false;
            string cachedPlayerName = "N/A";
            uint cachedObjectId = 0;

            _cacheLock.EnterReadLock();
            try
            {
                var cacheEntry = _cache[slotIndex];
                if (
                    cacheEntry.HasValue
                    && slotStillPopulatedAndValid
                    && cacheEntry.Value.ClientObjectId == currentObjectIdInSlot
                )
                {
                    performFetch = true;
                    cachedPlayerName = cacheEntry.Value.PlayerName;
                    cachedObjectId = cacheEntry.Value.ClientObjectId;
                }
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }

            string? intendedImagePath = null;
            bool shouldAttemptSave = false;
            byte[]? pixelDataForSave = null;
            int imageWidth = 0,
                imageHeight = 0;

            if (performFetch && cachedObjectId != 0)
            {
                fetchedData = _portraitDataService.TryGetDutyPortraitData(
                    slotIndex,
                    out fetchErrorMessage
                );

                if (fetchedData != null || string.IsNullOrEmpty(fetchErrorMessage))
                {
                    // Use our new compositing method, passing in the frame and decoration from our fetched data
                    using var image = _portraitCaptureService.CreateCompositedDutyPortraitImage(slotIndex,
                        fetchedData?.BannerFrame ?? 0,
                        fetchedData?.BannerDecoration ?? 0);
                    if (image != null)
                    {
                        shouldAttemptSave = true;
                        var timestamp = DateTime.UtcNow.Ticks;
                        var filename = $"{cachedObjectId}_{timestamp}.png";
                        intendedImagePath = Path.Combine(_tempPortraitFolder, filename);

                        try
                        {
                            imageWidth = image.Width;
                            imageHeight = image.Height;
                            pixelDataForSave = new byte[4 * imageWidth * imageHeight];
                            image.CopyPixelDataTo(pixelDataForSave); // This is ImageSharp's method
                        }
                        catch (Exception ex)
                        {
                            _log.Error(
                                ex,
                                $"Failed during pixel copy for image: {intendedImagePath}"
                            );
                            intendedImagePath = null;
                            shouldAttemptSave = false;
                            pixelDataForSave = null;
                        }
                    }
                    else
                    {
                        intendedImagePath = null;
                        shouldAttemptSave = false;
                    }
                }
                else
                {
                    intendedImagePath = null;
                    shouldAttemptSave = false;
                }
            }

            if (
                shouldAttemptSave
                && pixelDataForSave != null
                && !string.IsNullOrEmpty(intendedImagePath)
            )
            {
                var capturedLog = _log;
                var capturedTempFolder = _tempPortraitFolder;
                var capturedObjectId = cachedObjectId;
                var capturedPath = intendedImagePath;
                var capturedPixelData = pixelDataForSave;
                var capturedWidth = imageWidth;
                var capturedHeight = imageHeight;
                var capturedSemaphore = _imageSaveSemaphore;

                System.Threading.Tasks.Task.Run(() =>
                    ImageSaveHelper.ProcessImageSaveInBackgroundAsync(
                        capturedLog,
                        capturedTempFolder,
                        capturedObjectId,
                        capturedPath,
                        capturedPixelData,
                        capturedWidth,
                        capturedHeight,
                        capturedSemaphore
                    )
                );
            }

            if (performFetch)
            {
                _cacheLock.EnterWriteLock();
                try
                {
                    var currentCacheEntry = _cache[slotIndex];
                    if (
                        currentCacheEntry.HasValue
                        && currentCacheEntry.Value.ClientObjectId == cachedObjectId
                    )
                    {
                        _cache[slotIndex] = currentCacheEntry.Value.WithFullData(
                            fetchedData,
                            intendedImagePath
                        );
                    }
                }
                finally
                {
                    _cacheLock.ExitWriteLock();
                }
            }
        }
    }
}
