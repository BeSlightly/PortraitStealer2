using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace PortraitStealer.Services;

public unsafe partial class DutySlotCacheService : IDisposable
{
    private readonly IPluginLog _log;
    private readonly IFramework? _framework;
    private readonly IGameGui _gameGui;
    private readonly PortraitDataService _portraitDataService;
    private readonly PortraitCaptureService _portraitCaptureService;
    private readonly IDalamudPluginInterface _pluginInterface;

    private readonly CachedPortraitData?[] _cache = new CachedPortraitData?[8];
    private readonly ReaderWriterLockSlim _cacheLock = new(LockRecursionPolicy.NoRecursion);
    private bool _subscribed = false;

    private readonly ConcurrentQueue<int> _pendingFullDataQueue = new();

    private static readonly TimeSpan FetchDelay = TimeSpan.FromMilliseconds(750);

    private readonly string _tempPortraitFolder;

    public DutySlotCacheService(
        IPluginLog log,
        IFramework? framework,
        IGameGui gameGui,
        PortraitDataService portraitDataService,
        PortraitCaptureService portraitCaptureService,
        IDalamudPluginInterface pluginInterface
    )
    {
        _log = log;
        _framework = framework;
        _gameGui = gameGui;
        _portraitDataService = portraitDataService;
        _portraitCaptureService = portraitCaptureService;
        _pluginInterface = pluginInterface;

        _tempPortraitFolder = Path.Combine(
            _pluginInterface.ConfigDirectory.FullName,
            "CachedPortraits"
        );
        FileHelpers.SafeCreateDirectory(_tempPortraitFolder, _log, "Duty Portrait Cache");

        if (_framework != null)
        {
            _framework.Update += OnFrameworkUpdate;
            _subscribed = true;
            _log.Info("DutySlotCacheService subscribed.");
        }
        else
        {
            _log.Error("Framework service is null, DutySlotCacheService disabled.");
        }
    }

    public void Dispose()
    {
        if (_framework != null && _subscribed)
        {
            _framework.Update -= OnFrameworkUpdate;
            _subscribed = false;
            _log.Info("DutySlotCacheService unsubscribed.");
        }
        ClearCache();
        _cacheLock.Dispose(); // Dispose ReaderWriterLockSlim
    }

    public CachedPortraitData?[] GetCurrentCacheState()
    {
        _cacheLock.EnterReadLock();
        try
        {
            var copy = new CachedPortraitData?[8];
            Array.Copy(_cache, copy, 8);
            return copy;
        }
        finally
        {
            _cacheLock.ExitReadLock();
        }
    }

    public void ClearCache()
    {
        _cacheLock.EnterWriteLock();
        try
        {
            for (int i = 0; i < 8; i++)
            {
                FileHelpers.SafeDeleteFile(_cache[i]?.TemporaryImagePath, _log, $"Manual Clear (Slot {i + 1})");
                _cache[i] = null;
            }
        }
        finally
        {
            _cacheLock.ExitWriteLock();
        }

        while (_pendingFullDataQueue.TryDequeue(out _)) { }
        _log.Info("Duty slot cache manually cleared.");
    }


}
