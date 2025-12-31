using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using PortraitStealer.Services;
using PortraitStealer.Windows;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PortraitStealer;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    private static ICommandManager CommandManager { get; set; } = null!;

    [PluginService]
    private static IDataManager DataManager { get; set; } = null!;

    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    [PluginService]
    private static IFramework Framework { get; set; } = null!;

    [PluginService]
    internal static ITextureProvider TextureProvider { get; private set; } = null!;

    [PluginService]
    internal static IGameGui GameGui { get; private set; } = null!;

    [PluginService]
    internal static IGameInteropProvider InteropProvider { get; private set; } = null!;

    [PluginService]
    internal static IPlayerState PlayerState { get; private set; } = null!;

    public readonly PortraitDataService PortraitDataService;
    private readonly PresetSerializer _presetSerializer;
    public readonly DutySlotCacheService DutySlotCacheService;
    private readonly PortraitCaptureService _portraitCaptureService;
    public readonly AdventurerPlateCacheService AdventurerPlateCacheService;
    public readonly Configuration Configuration;
    private volatile bool _pendingAdventurerPlateCopy;

    private const string CommandName = "/psteal";
    private readonly WindowSystem _windowSystem = new("PortraitStealer");
    private readonly MainWindow _mainWindow;
    private readonly AdventurerPlateCaptureOverlay _adventurerPlateCaptureOverlay;

    public StolenPortraitInfo? PlateStolenInfo { get; private set; } = null;
    public string? PlateErrorMessage { get; private set; } = null;
    
    private readonly SemaphoreSlim _adventurerPlateSemaphore = new(1, 1);
    private readonly CancellationTokenSource _disposalCancellationTokenSource = new();
    public bool IsCapturingAdventurerPlate { get; private set; } = false;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        PortraitDataService = new PortraitDataService(DataManager, Log, GameGui, PlayerState);
        _presetSerializer = new PresetSerializer(Log);
        _portraitCaptureService = new PortraitCaptureService(Log, PluginInterface, GameGui, DataManager, TextureProvider, InteropProvider);
        DutySlotCacheService = new DutySlotCacheService(
            Log,
            Framework,
            GameGui,
            PortraitDataService,
            _portraitCaptureService,
            PluginInterface
        );
        AdventurerPlateCacheService = new AdventurerPlateCacheService(Log, PluginInterface);
        _mainWindow = new MainWindow(this);
        _adventurerPlateCaptureOverlay = new AdventurerPlateCaptureOverlay(this);
        _windowSystem.AddWindow(_mainWindow);
        _windowSystem.AddWindow(_adventurerPlateCaptureOverlay);
        CommandManager.AddHandler(
            CommandName,
            new CommandInfo(OnCommand) { HelpMessage = "Toggle Portrait Stealer window" }
        );
        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUI;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUI;
        Log.Info("PortraitStealer Plugin Loaded.");
    }

    public void Dispose()
    {
        _disposalCancellationTokenSource.Cancel();
        
        try
        {
            if (IsCapturingAdventurerPlate)
            {
                Log.Info("Waiting for adventurer plate capture to complete before disposal...");
                var waitTask = Task.Run(async () =>
                {
                    var timeout = TimeSpan.FromSeconds(2);
                    var start = DateTime.UtcNow;
                    while (IsCapturingAdventurerPlate && DateTime.UtcNow - start < timeout)
                    {
                        await Task.Delay(50);
                    }
                });
                waitTask.Wait(2500);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Exception while waiting for background tasks during disposal");
        }

        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUI;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUI;
        CommandManager.RemoveHandler(CommandName);
        _windowSystem.RemoveAllWindows();
        _mainWindow?.Dispose();
        _adventurerPlateCaptureOverlay?.Dispose();
        DutySlotCacheService?.Dispose();
        _portraitCaptureService?.Dispose();
        AdventurerPlateCacheService?.Dispose();
        _adventurerPlateSemaphore?.Dispose();
        _disposalCancellationTokenSource?.Dispose();
        Log.Info("PortraitStealer Plugin Unloaded.");
    }

    private void OnCommand(string command, string args) => ToggleMainUI();

    private void DrawUI()
    {
        DutySlotCacheService?.ProcessPendingFullDataRequests();
        _windowSystem.Draw();
    }

    public void ToggleMainUI() => _mainWindow.Toggle();

    public void StealAdventurerPlateData()
    {
        if (IsCapturingAdventurerPlate || _disposalCancellationTokenSource.Token.IsCancellationRequested)
        {
            PlateErrorMessage = IsCapturingAdventurerPlate ? "Capture already in progress..." : "Plugin is shutting down...";
            return;
        }

        _ = Task.Run(async () => await StealAdventurerPlateDataAsync(_disposalCancellationTokenSource.Token));
    }

    private async Task StealAdventurerPlateDataAsync(CancellationToken cancellationToken)
    {
        await _adventurerPlateSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                PlateErrorMessage = "Capture cancelled due to plugin shutdown";
                return;
            }

            IsCapturingAdventurerPlate = true;
            ClearPlateState();

            StolenPortraitInfo? info = null;
            string? errorMessage = null;
            byte[]? pixelData = null;
            int imageWidth = 0, imageHeight = 0;
            string? imagePath = null;
            nint plateTexturePtr = nint.Zero;

            try
            {
                await Framework.RunOnFrameworkThread(() =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        errorMessage = "Capture cancelled";
                        return;
                    }

                    unsafe
                    {
                        try
                        {
                            if (PortraitDataService == null || _portraitCaptureService == null || AdventurerPlateCacheService == null)
                            {
                                errorMessage = "Services unavailable (plugin may be shutting down)";
                                return;
                            }

                            info = PortraitDataService.TryGetAdventurerPlateData(out errorMessage);
                            if (info == null) return;

                            if (info.Value.AdventurerPlateTexture == null || info.Value.AdventurerPlateTexture->D3D11Texture2D == null)
                            {
                                errorMessage = "Portrait texture not found on Adventurer Plate.";
                                return;
                            }

                            plateTexturePtr = (nint)info.Value.AdventurerPlateTexture->D3D11Texture2D;
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Error during adventurer plate data extraction on UI thread");
                            errorMessage = $"Error capturing data: {ex.Message}";
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error calling Framework.RunOnFrameworkThread");
                errorMessage = $"Framework error: {ex.Message}";
            }

            if (cancellationToken.IsCancellationRequested)
            {
                PlateErrorMessage = "Capture cancelled";
                return;
            }

            if (info == null || !string.IsNullOrEmpty(errorMessage))
            {
                PlateErrorMessage = errorMessage ?? "Unknown error occurred";
                return;
            }

            if (plateTexturePtr == nint.Zero)
            {
                PlateErrorMessage = "Portrait texture not found on Adventurer Plate.";
                return;
            }

            Image<Bgra32>? baseImage = null;
            Image<Bgra32>? image = null;
            try
            {
                baseImage = await _portraitCaptureService.CaptureAdventurerPlateImageAsync(plateTexturePtr, cancellationToken);
                if (baseImage == null)
                {
                    PlateErrorMessage = "Failed to capture adventurer plate image.";
                    return;
                }

                if (info.Value.IsAdventurerPlate)
                {
                    image = _portraitCaptureService.CreateCompositedAdventurerPlateImageFromIcons(
                        baseImage,
                        info.Value.BannerFrame,
                        info.Value.BannerDecoration
                    );
                }

                if (image == null)
                {
                    image = baseImage;
                    baseImage = null;
                }

                imageWidth = image.Width;
                imageHeight = image.Height;
                pixelData = new byte[4 * imageWidth * imageHeight];
                image.CopyPixelDataTo(pixelData);
            }
            finally
            {
                baseImage?.Dispose();
                image?.Dispose();
            }

            var cacheDir = AdventurerPlateCacheService.GetCacheDirectory();
            var playerName = info.Value.PlayerName ?? "Unknown";
            var safePlayerName = string.Join("_", playerName.Split(Path.GetInvalidFileNameChars()));
            var fileName = $"{safePlayerName}_{DateTime.Now:yyyyMMddHHmmss}.png";
            imagePath = Path.Combine(cacheDir, fileName);

            if (pixelData == null || string.IsNullOrEmpty(imagePath))
            {
                PlateErrorMessage = "Failed to extract image data";
                return;
            }

            try
            {
                await SaveAdventurerPlateImageAsync(pixelData, imageWidth, imageHeight, imagePath, cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    PlateErrorMessage = "Capture cancelled";
                    return;
                }

                if (AdventurerPlateCacheService != null)
                {
                    var infoWithPath = info.Value.WithImagePath(imagePath);
                    PlateStolenInfo = infoWithPath;

                    AdventurerPlateCacheService.AddToCache(infoWithPath, imagePath);

                    Log.Info($"Successfully captured adventurer plate for {info.Value.PlayerName}");
                }
            }
            catch (OperationCanceledException)
            {
                PlateErrorMessage = "Capture cancelled";
                Log.Info("Adventurer plate capture was cancelled");
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Failed to save adventurer plate image: {imagePath}");
                PlateErrorMessage = $"Failed to save image: {ex.Message}";
            }
        }
        catch (OperationCanceledException)
        {
            PlateErrorMessage = "Capture cancelled";
            Log.Info("Adventurer plate capture was cancelled during semaphore wait");
        }
        finally
        {
            IsCapturingAdventurerPlate = false;
            _adventurerPlateSemaphore.Release();
        }
    }

    private async Task SaveAdventurerPlateImageAsync(byte[] pixelData, int width, int height, string imagePath, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(imagePath);
        if (directory != null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var image = Image.LoadPixelData<Bgra32>(pixelData, width, height);
        await Task.Run(() => 
        {
            cancellationToken.ThrowIfCancellationRequested();
            image.SaveAsPng(imagePath);
        }, cancellationToken);
    }

    public string? GeneratePresetString(StolenPortraitInfo info) =>
        _presetSerializer.GeneratePresetString(info);

    internal void RequestAdventurerPlateCopyAfterCapture()
    {
        _pendingAdventurerPlateCopy = true;
    }

    internal bool TryConsumePendingAdventurerPlateCopy(out StolenPortraitInfo info)
    {
        if (!_pendingAdventurerPlateCopy || PlateStolenInfo == null || IsCapturingAdventurerPlate)
        {
            info = default;
            return false;
        }

        _pendingAdventurerPlateCopy = false;
        info = PlateStolenInfo.Value;
        return true;
    }

    internal bool HasPendingAdventurerPlateCopy => _pendingAdventurerPlateCopy;

    internal void ClearPendingAdventurerPlateCopy()
    {
        _pendingAdventurerPlateCopy = false;
    }

    public void ClearPlateState()
    {
        PlateStolenInfo = null;
        PlateErrorMessage = null;
    }

    public void ClearDutyCache() => DutySlotCacheService?.ClearCache();
    
    public void ClearAdventurerPlateCache()
    {
        AdventurerPlateCacheService?.ClearCache();
        ClearPlateState();
    }
}
