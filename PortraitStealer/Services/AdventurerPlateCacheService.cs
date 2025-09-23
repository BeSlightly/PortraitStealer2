using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace PortraitStealer.Services;

public class AdventurerPlateCacheService : IDisposable
{
    private readonly IPluginLog _log;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly List<CachedAdventurerPlateInfo> _cache = new();
    private readonly object _cacheLock = new();
    private readonly string _adventurerPlateFolder;
    private const int MaxCacheSize = 10;

    public AdventurerPlateCacheService(IPluginLog log, IDalamudPluginInterface pluginInterface)
    {
        _log = log;
        _pluginInterface = pluginInterface;
        
        _adventurerPlateFolder = Path.Combine(
            _pluginInterface.ConfigDirectory.FullName,
            "AdventurerPlates"
        );
        
        FileHelpers.SafeCreateDirectory(_adventurerPlateFolder, _log, "Adventurer Plate Cache");

        CleanupOldFiles();
        CleanupLegacyFiles();
    }

    public void Dispose()
    {
        lock (_cacheLock)
        {
            _cache.Clear();
        }
    }

    public void AddToCache(StolenPortraitInfo info, string imagePath)
    {
        if (string.IsNullOrEmpty(info.PlayerName))
            return;

        lock (_cacheLock)
        {
            var existingIndex = _cache.FindIndex(c => c.PlayerName == info.PlayerName);
            if (existingIndex >= 0)
            {
                var existing = _cache[existingIndex];
                FileHelpers.SafeDeleteFile(existing.ImagePath, _log, $"Replacing cache for {info.PlayerName}");
                _cache.RemoveAt(existingIndex);
            }

            var cacheInfo = new CachedAdventurerPlateInfo(
                info.PlayerName,
                info.ClassJobId,
                info.ClassJobAbbreviation,
                imagePath,
                info.Timestamp,
                info
            );
            
            _cache.Insert(0, cacheInfo);

            while (_cache.Count > MaxCacheSize)
            {
                var oldest = _cache[_cache.Count - 1];
                FileHelpers.SafeDeleteFile(oldest.ImagePath, _log, $"Cache limit exceeded, removing {oldest.PlayerName}");
                _cache.RemoveAt(_cache.Count - 1);
            }
            
            _log.Debug($"Added {info.PlayerName} to adventurer plate cache. Cache size: {_cache.Count}");
        }
    }

    public List<CachedAdventurerPlateInfo> GetCachedPortraits()
    {
        lock (_cacheLock)
        {
            return new List<CachedAdventurerPlateInfo>(_cache);
        }
    }

    public void ClearCache()
    {
        lock (_cacheLock)
        {
            foreach (var entry in _cache)
            {
                FileHelpers.SafeDeleteFile(entry.ImagePath, _log, "Manual cache clear");
            }
            _cache.Clear();
        }
        _log.Info("Adventurer plate cache cleared.");
    }

    private void CleanupOldFiles()
    {
        try
        {
            if (!Directory.Exists(_adventurerPlateFolder))
                return;

            var files = Directory.GetFiles(_adventurerPlateFolder, "*.png");
            var currentCacheFiles = new HashSet<string>(_cache.Select(c => c.ImagePath));
            
            foreach (var file in files)
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.CreationTime < DateTime.Now.AddDays(-7) || !currentCacheFiles.Contains(file))
                    {
                        File.Delete(file);
                        _log.Debug($"Deleted old/orphaned adventurer plate file: {Path.GetFileName(file)}");
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, $"Failed to delete old file: {Path.GetFileName(file)}");
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to cleanup old adventurer plate files.");
        }
    }



    public string GetCacheDirectory() => _adventurerPlateFolder;

    private void CleanupLegacyFiles()
    {
        try
        {
            var configDir = _pluginInterface.ConfigDirectory.FullName;
            var legacyFiles = Directory.GetFiles(configDir, "adventurer_plate_*.png");
            
            foreach (var file in legacyFiles)
            {
                try
                {
                    File.Delete(file);
                    _log.Info($"Deleted legacy adventurer plate file: {Path.GetFileName(file)}");
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, $"Failed to delete legacy file: {Path.GetFileName(file)}");
                }
            }
            
            if (legacyFiles.Length > 0)
            {
                _log.Info($"Cleaned up {legacyFiles.Length} legacy adventurer plate files.");
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to cleanup legacy adventurer plate files.");
        }
    }
}

public struct CachedAdventurerPlateInfo
{
    public readonly string PlayerName;
    public readonly byte ClassJobId;
    public readonly string ClassJobAbbreviation;
    public readonly string ImagePath;
    public readonly DateTime Timestamp;
    public readonly StolenPortraitInfo PortraitInfo;

    public CachedAdventurerPlateInfo(
        string playerName,
        byte classJobId,
        string classJobAbbreviation,
        string imagePath,
        DateTime timestamp,
        StolenPortraitInfo portraitInfo)
    {
        PlayerName = playerName;
        ClassJobId = classJobId;
        ClassJobAbbreviation = classJobAbbreviation;
        ImagePath = imagePath;
        Timestamp = timestamp;
        PortraitInfo = portraitInfo;
    }
}
