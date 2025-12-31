using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Data.Files;
using Lumina.Excel.Sheets;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using Texture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace PortraitStealer.Services;

public unsafe class PortraitCaptureService : IDisposable
{
    private readonly IPluginLog _log;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IGameGui _gameGui;
    private readonly IDataManager _dataManager;
    private readonly ITextureProvider _textureProvider;
    private readonly IGameInteropProvider _interopProvider;
    private readonly nint _deviceHandle;

    private readonly object _captureLock = new();
    private readonly object _pendingCaptureLock = new();
    private PendingCapture? _pendingCapture;
    private static readonly TimeSpan CaptureDelay = TimeSpan.FromMilliseconds(200);

    private delegate void ImmediateContextProcessCommands(ImmediateContext* commands, RenderCommandBufferGroup* bufferGroup, uint a3);

    [Signature("E8 ?? ?? ?? ?? 48 8B 4B 30 FF 15 ?? ?? ?? ??", DetourName = nameof(OnImmediateContextProcessCommands))]
    private readonly Hook<ImmediateContextProcessCommands>? _immediateContextProcessCommandsHook = null;

    public PortraitCaptureService(IPluginLog log, IDalamudPluginInterface pluginInterface, IGameGui gameGui, IDataManager dataManager, ITextureProvider textureProvider, IGameInteropProvider interopProvider)
    {
        _log = log;
        _pluginInterface = pluginInterface;
        _gameGui = gameGui;
        _dataManager = dataManager;
        _textureProvider = textureProvider;
        _interopProvider = interopProvider;

        _deviceHandle = _pluginInterface.UiBuilder.DeviceHandle;
        if (_deviceHandle == nint.Zero)
            _log.Error("Failed to get D3D11 device handle. Portrait capture will not work.");

        try
        {
            _interopProvider.InitializeFromAttributes(this);
            _immediateContextProcessCommandsHook?.Enable();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to initialize render thread hook for portrait capture.");
        }
    }

    public void Dispose()
    {
        _immediateContextProcessCommandsHook?.Dispose();
        PendingCapture? pending = null;
        lock (_pendingCaptureLock)
        {
            pending = _pendingCapture;
            _pendingCapture = null;
        }
        pending?.Complete(null);
    }

    public Image<Bgra32>? GetAdventurerPlateImage(Texture* portraitTexture)
    {
        if (_deviceHandle == nint.Zero) return null;
        if (portraitTexture == null || portraitTexture->D3D11Texture2D == null) return null;

        var texture = (ID3D11Texture2D*)portraitTexture->D3D11Texture2D;
        lock (_captureLock)
        {
            return ReadTextureToImage(texture, "Adventurer Plate");
        }
    }

    public Image<Bgra32>? GetDutyPortraitImage(int partySlotIndex)
    {
        if (_deviceHandle == nint.Zero) return null;
        if (partySlotIndex < 0 || partySlotIndex > 7) return null;

        var agentInterface = _gameGui.GetAgentById((int)AgentId.BannerParty);
        if (agentInterface.IsNull || !agentInterface.IsAgentActive) return null;
        var agentBannerInterface = (AgentBannerInterface*)agentInterface.Address;
        var storage = agentBannerInterface->Data;
        if (storage == null) return null;

        ref var characterData = ref storage->Characters[partySlotIndex];
        if (characterData.CharaView.ClientObjectId == 0 || characterData.CharaView.ClientObjectId == 0xE0000000) return null;

        var renderTargetManager = RenderTargetManager.Instance();
        if (renderTargetManager == null) return null;

        var charaViewTexture = renderTargetManager->GetCharaViewTexture((uint)partySlotIndex);
        if (charaViewTexture == null || charaViewTexture->D3D11Texture2D == null) return null;

        var texture = (ID3D11Texture2D*)charaViewTexture->D3D11Texture2D;
        lock (_captureLock)
        {
            return ReadTextureToImage(texture, $"Duty Portrait (Slot {partySlotIndex + 1})");
        }
    }

    public Task<Image<Bgra32>?> CaptureAdventurerPlateImageAsync(nint texturePointer, CancellationToken cancellationToken)
    {
        if (_deviceHandle == nint.Zero || texturePointer == nint.Zero)
        {
            return Task.FromResult<Image<Bgra32>?>(null);
        }

        if (_immediateContextProcessCommandsHook == null)
        {
            Image<Bgra32>? image;
            lock (_captureLock)
            {
                image = ReadTextureToImage((ID3D11Texture2D*)texturePointer, "Adventurer Plate");
            }
            return Task.FromResult(image);
        }

        var sourceTexture = (ID3D11Texture2D*)texturePointer;
        sourceTexture->AddRef();
        var pending = new PendingCapture(sourceTexture, "Adventurer Plate", cancellationToken);

        lock (_pendingCaptureLock)
        {
            if (_pendingCapture != null)
            {
                pending.Complete(null);
                return Task.FromResult<Image<Bgra32>?>(null);
            }

            _pendingCapture = pending;
        }

        return pending.Task;
    }

    private void OnImmediateContextProcessCommands(ImmediateContext* commands, RenderCommandBufferGroup* bufferGroup, uint a3)
    {
        try
        {
            TryProcessPendingCapture();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Exception during OnImmediateContextProcessCommands");
        }

        _immediateContextProcessCommandsHook!.Original(commands, bufferGroup, a3);
    }

    private void TryProcessPendingCapture()
    {
        PendingCapture? pending = null;
        lock (_pendingCaptureLock)
        {
            if (_pendingCapture == null)
                return;

            if (DateTime.UtcNow - _pendingCapture.RequestedAtUtc < CaptureDelay)
                return;

            pending = _pendingCapture;
            _pendingCapture = null;
        }

        if (pending == null)
            return;

        if (pending.CancellationToken.IsCancellationRequested)
        {
            pending.Complete(null);
            return;
        }

        Image<Bgra32>? image = null;
        try
        {
            lock (_captureLock)
            {
                image = ReadTextureToImage(pending.SourceTexture, pending.Context);
            }
        }
        finally
        {
            pending.Complete(image);
        }
    }

    private Image<Bgra32>? ReadTextureToImage(ID3D11Texture2D* texture, string captureContext)
    {
        if (_deviceHandle == nint.Zero || texture == null) return null;

        var device = (ID3D11Device*)_deviceHandle;
        if (device == null)
        {
            _log.Error($"ReadTextureToImage: Device is null for {captureContext}.");
            return null;
        }

        D3D11_TEXTURE2D_DESC desc;
        texture->GetDesc(&desc);
        
        if (desc.SampleDesc.Count != 1)
        {
            _log.Warning($"ReadTextureToImage: {captureContext} texture is multisampled (SampleDesc.Count = {desc.SampleDesc.Count}); not supported.");
            return null;
        }

        if (desc.Format == DXGI_FORMAT.DXGI_FORMAT_BC7_UNORM)
        {
            _log.Warning($@"Unsupported compressed format for {captureContext}: {desc.Format}");
            return null;
        }
        
        if (desc.Format != DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM && desc.Format != DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM)
        {
            _log.Warning($"ReadTextureToImage: {captureContext} texture has unsupported format ({desc.Format})");
            return null;
        }

        var stagingDesc = new D3D11_TEXTURE2D_DESC
        {
            ArraySize = 1,
            BindFlags = 0,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
            Format = desc.Format,
            Height = desc.Height,
            Width = desc.Width,
            MipLevels = 1,
            MiscFlags = desc.MiscFlags,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING
        };

        ID3D11Texture2D* stagingTexture;
        var hr = device->CreateTexture2D(&stagingDesc, null, &stagingTexture);
        if (hr < 0 || stagingTexture == null)
        {
            _log.Warning($"ReadTextureToImage: Failed to create staging texture for {captureContext}. HRESULT: 0x{(int)hr:X8}");
            return null;
        }

        ID3D11DeviceContext* deviceContext = null;
        device->GetImmediateContext(&deviceContext);
        if (deviceContext == null)
        {
            stagingTexture->Release();
            return null;
        }

        deviceContext->CopyResource((ID3D11Resource*)stagingTexture, (ID3D11Resource*)texture);

        D3D11_MAPPED_SUBRESOURCE mapped;
        hr = deviceContext->Map((ID3D11Resource*)stagingTexture, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
        if (hr < 0 || mapped.pData == null)
        {
            deviceContext->Release();
            stagingTexture->Release();
            return null;
        }

        try
        {
            var rowPitch = (int)mapped.RowPitch;
            var height = (int)desc.Height;
            var width = (int)desc.Width;
            const int bytesPerPixel = 4;

            var result = new byte[width * height * bytesPerPixel];
            var srcPtr = (byte*)mapped.pData;

            for (var y = 0; y < height; y++)
            {
                Marshal.Copy((nint)(srcPtr + y * rowPitch), result, y * width * bytesPerPixel, width * bytesPerPixel);
            }

            if (desc.Format == DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM)
            {
                using var rgbaImage = Image.LoadPixelData<Rgba32>(result, width, height);
                return rgbaImage.CloneAs<Bgra32>();
            }

            return Image.LoadPixelData<Bgra32>(result, width, height);
        }
        finally
        {
            deviceContext->Unmap((ID3D11Resource*)stagingTexture, 0);
            deviceContext->Release();
            stagingTexture->Release();
        }
    }

    private sealed class PendingCapture
    {
        private ID3D11Texture2D* _sourceTexture;
        private readonly TaskCompletionSource<Image<Bgra32>?> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingCapture(ID3D11Texture2D* sourceTexture, string context, CancellationToken cancellationToken)
        {
            _sourceTexture = sourceTexture;
            Context = context;
            CancellationToken = cancellationToken;
            RequestedAtUtc = DateTime.UtcNow;
        }

        public ID3D11Texture2D* SourceTexture => _sourceTexture;
        public string Context { get; }
        public CancellationToken CancellationToken { get; }
        public DateTime RequestedAtUtc { get; }
        public Task<Image<Bgra32>?> Task => _tcs.Task;

        public void Complete(Image<Bgra32>? image)
        {
            _tcs.TrySetResult(image);
            if (_sourceTexture != null)
            {
                _sourceTexture->Release();
                _sourceTexture = null;
            }
        }
    }


    public Image<Bgra32>? CreateCompositedAdventurerPlateImageFromIcons(Texture* portraitTexture, ushort bannerFrame, ushort bannerDecoration)
    {
        try
        {
            _log.Debug($"CreateCompositedAdventurerPlateImageFromIcons: Loading icons for BannerFrame {bannerFrame}, BannerDecoration {bannerDecoration}");
            
            using var portraitImage = GetAdventurerPlateImage(portraitTexture);
            if (portraitImage == null)
            {
                _log.Warning("CreateCompositedAdventurerPlateImageFromIcons: Failed to get portrait image");
                return null;
            }

            return CreateCompositedAdventurerPlateImageFromIcons(portraitImage, bannerFrame, bannerDecoration);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "CreateCompositedAdventurerPlateImageFromIcons: Exception during icon-based compositing");
            return null;
        }
    }

    public Image<Bgra32>? CreateCompositedAdventurerPlateImageFromIcons(Image<Bgra32> portraitImage, ushort bannerFrame, ushort bannerDecoration)
    {
        try
        {
            Image<Bgra32>? borderImage = null;
            if (bannerFrame != 0)
            {
                var bannerFrameSheet = _dataManager.GetExcelSheet<BannerFrame>();
                if (bannerFrameSheet != null && bannerFrameSheet.TryGetRow(bannerFrame, out var bannerFrameRow) && bannerFrameRow.Image != 0)
                {
                    borderImage = LoadIconAsImage((uint)bannerFrameRow.Image, "Border");
                    if (borderImage != null)
                    {
                        _log.Debug($"CreateCompositedAdventurerPlateImageFromIcons: Successfully loaded border icon {bannerFrameRow.Image}");
                    }
                }
            }

            Image<Bgra32>? decorationImage = null;
            if (bannerDecoration != 0)
            {
                var bannerDecorationSheet = _dataManager.GetExcelSheet<BannerDecoration>();
                if (bannerDecorationSheet != null && bannerDecorationSheet.TryGetRow(bannerDecoration, out var bannerDecorationRow) && bannerDecorationRow.Image != 0)
                {
                    decorationImage = LoadIconAsImage((uint)bannerDecorationRow.Image, "Decoration");
                    if (decorationImage != null)
                    {
                        _log.Debug($"CreateCompositedAdventurerPlateImageFromIcons: Successfully loaded decoration icon {bannerDecorationRow.Image}");
                    }
                }
            }

            var compositedImage = new Image<Bgra32>(portraitImage.Width, portraitImage.Height);
            compositedImage.Mutate(ctx => ctx.DrawImage(portraitImage, new Point(0, 0), 1f));
            _log.Debug("CreateCompositedAdventurerPlateImageFromIcons: Applied portrait layer");

            if (borderImage != null)
            {
                if (borderImage.Width != portraitImage.Width || borderImage.Height != portraitImage.Height)
                {
                    borderImage.Mutate(x => x.Resize(portraitImage.Width, portraitImage.Height));
                }
                compositedImage.Mutate(ctx => ctx.DrawImage(borderImage, new Point(0, 0), 1f));
                _log.Debug("CreateCompositedAdventurerPlateImageFromIcons: Applied border layer");
                borderImage.Dispose();
            }

            if (decorationImage != null)
            {
                if (decorationImage.Width != portraitImage.Width || decorationImage.Height != portraitImage.Height)
                {
                    decorationImage.Mutate(x => x.Resize(portraitImage.Width, portraitImage.Height));
                }
                compositedImage.Mutate(ctx => ctx.DrawImage(decorationImage, new Point(0, 0), 1f));
                _log.Debug("CreateCompositedAdventurerPlateImageFromIcons: Applied decoration layer");
                decorationImage.Dispose();
            }

            _log.Debug("CreateCompositedAdventurerPlateImageFromIcons: Successfully composited adventurer plate using icon approach");
            return compositedImage;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "CreateCompositedAdventurerPlateImageFromIcons: Exception during icon-based compositing");
            return null;
        }
    }

    public Image<Bgra32>? CreateCompositedDutyPortraitImage(int partySlotIndex, ushort bannerFrame, ushort bannerDecoration)
    {
        try
        {
            var portraitImage = GetDutyPortraitImage(partySlotIndex);
            if (portraitImage == null)
            {
                _log.Warning($"CreateCompositedDutyPortraitImage: Failed to get base portrait for slot {partySlotIndex + 1}");
                return null;
            }

            Image<Bgra32>? borderImage = null;
            if (bannerFrame != 0)
            {
                var bannerFrameSheet = _dataManager.GetExcelSheet<BannerFrame>();
                if (bannerFrameSheet != null && bannerFrameSheet.TryGetRow(bannerFrame, out var bannerFrameRow) && bannerFrameRow.Image != 0)
                {
                    borderImage = LoadIconAsImage((uint)bannerFrameRow.Image, "Border");
                }
            }

            Image<Bgra32>? decorationImage = null;
            if (bannerDecoration != 0)
            {
                var bannerDecorationSheet = _dataManager.GetExcelSheet<BannerDecoration>();
                if (bannerDecorationSheet != null && bannerDecorationSheet.TryGetRow(bannerDecoration, out var bannerDecorationRow) && bannerDecorationRow.Image != 0)
                {
                    decorationImage = LoadIconAsImage((uint)bannerDecorationRow.Image, "Decoration");
                }
            }
            var compositedImage = new Image<Bgra32>(portraitImage.Width, portraitImage.Height);
            compositedImage.Mutate(ctx => ctx.DrawImage(portraitImage, new Point(0, 0), 1f));

            if (borderImage != null)
            {
                borderImage.Mutate(x => x.Resize(portraitImage.Width, portraitImage.Height));
                compositedImage.Mutate(ctx => ctx.DrawImage(borderImage, new Point(0, 0), 1f));
                borderImage.Dispose();
            }

            if (decorationImage != null)
            {
                decorationImage.Mutate(x => x.Resize(portraitImage.Width, portraitImage.Height));
                compositedImage.Mutate(ctx => ctx.DrawImage(decorationImage, new Point(0, 0), 1f));
                decorationImage.Dispose();
            }

            portraitImage.Dispose();
            return compositedImage;
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"CreateCompositedDutyPortraitImage: Exception during compositing for slot {partySlotIndex + 1}");
            return null;
        }
    }

    private Image<Bgra32>? LoadIconAsImage(uint iconId, string context)
    {
        try
        {
            if (_textureProvider.TryGetIconPath(iconId, out var iconPath))
            {
                var texFile = _dataManager.GetFile<TexFile>(iconPath);
                if (texFile != null)
                {
                    var imageData = texFile.ImageData;
                    var image = Image.LoadPixelData<Bgra32>(imageData, texFile.Header.Width, texFile.Header.Height);
                    _log.Debug($"LoadIconAsImage: Successfully loaded {context} icon {iconId} ({texFile.Header.Width}x{texFile.Header.Height})");
                    return image;
                }
            }
            
            _log.Warning($"LoadIconAsImage: Failed to load {context} icon {iconId}");
            return null;
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"LoadIconAsImage: Exception loading {context} icon {iconId}");
            return null;
        }
    }


}
