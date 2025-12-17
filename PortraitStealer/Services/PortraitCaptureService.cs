using System;
using System.Buffers;
using System.IO;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
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
    private readonly nint _deviceHandle;
    private DateTime _suppressGpuUntil = DateTime.MinValue;

    private ID3D11Texture2D* _reusableStagingTexture;
    private D3D11_TEXTURE2D_DESC _reusableStagingDesc;
    private readonly object _stagingTextureLock = new();

    public PortraitCaptureService(IPluginLog log, IDalamudPluginInterface pluginInterface, IGameGui gameGui, IDataManager dataManager, ITextureProvider textureProvider)
    {
        _log = log;
        _pluginInterface = pluginInterface;
        _gameGui = gameGui;
        _dataManager = dataManager;
        _textureProvider = textureProvider;

        _deviceHandle = _pluginInterface.UiBuilder.DeviceHandle;
        if (_deviceHandle == nint.Zero)
            _log.Error("Failed to get D3D11 device handle. Portrait capture will not work.");
    }

    public void Dispose()
    {
        lock (_stagingTextureLock)
        {
            if (_reusableStagingTexture != null)
            {
                _reusableStagingTexture->Release();
                _reusableStagingTexture = null;
            }
        }
    }

    public Image<Bgra32>? GetAdventurerPlateImage(Texture* portraitTexture)
    {
        if (_deviceHandle == nint.Zero) return null;
        if (portraitTexture == null || portraitTexture->D3D11Texture2D == null) return null;

        var texture = (ID3D11Texture2D*)portraitTexture->D3D11Texture2D;
        return ProcessTexture(texture, "Adventurer Plate");
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
        return ProcessTexture(texture, $"Duty Portrait (Slot {partySlotIndex + 1})");
    }

    private Image<Bgra32>? ProcessTexture(ID3D11Texture2D* texture, string captureContext)
    {
        byte[]? rentedBuffer = null;
        byte[]? rentedTightBuffer = null;
        D3D11_MAPPED_SUBRESOURCE mapped = default;
        bool isMapped = false;
        ID3D11Texture2D* currentStagingTexture = null;
        ID3D11DeviceContext* deviceContext = null;

        try
        {
            if (DateTime.UtcNow < _suppressGpuUntil)
            {
                _log.Debug($"ProcessTexture: Suppressing GPU work during cooldown for {captureContext}.");
                return null;
            }

            var device = (ID3D11Device*)_deviceHandle;
            if (device == null)
            {
                _log.Error($"ProcessTexture: Device is null for {captureContext}.");
                return null;
            }

            if (texture == null)
                return null;

            D3D11_TEXTURE2D_DESC desc;
            texture->GetDesc(&desc);
            
            if (desc.SampleDesc.Count != 1)
            {
                _log.Warning($"ProcessTexture: {captureContext} texture is multisampled (SampleDesc.Count = {desc.SampleDesc.Count}); not supported.");
                return null;
            }

            if (desc.Format == DXGI_FORMAT.DXGI_FORMAT_BC7_UNORM)
            {
                _log.Warning($@"Unsupported compressed format for {captureContext}: {desc.Format}");
                return null;
            }
            
            if (desc.Format != DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM && desc.Format != DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM)
            {
                _log.Warning($"ProcessTexture: {captureContext} texture has unsupported format ({desc.Format})");
                return null;
            }

            lock (_stagingTextureLock)
            {
                if (_reusableStagingTexture == null || _reusableStagingDesc.Width != desc.Width || _reusableStagingDesc.Height != desc.Height || _reusableStagingDesc.Format != desc.Format)
                {
                    if (_reusableStagingTexture != null)
                    {
                        _reusableStagingTexture->Release();
                        _reusableStagingTexture = null;
                    }

                    _reusableStagingDesc = desc;
                    _reusableStagingDesc.MipLevels = 1;
                    _reusableStagingDesc.ArraySize = 1;
                    _reusableStagingDesc.SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 };
                    _reusableStagingDesc.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
                    _reusableStagingDesc.BindFlags = 0;
                    _reusableStagingDesc.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
                    _reusableStagingDesc.MiscFlags = 0;

                    try
                    {
                        ID3D11Texture2D* stagingTexture;
                        var stagingDesc = _reusableStagingDesc;
                        var hr = device->CreateTexture2D(&stagingDesc, null, &stagingTexture);
                        if (hr < 0 || stagingTexture == null)
                        {
                            _log.Error($"Failed to create/resize reusable staging texture for {captureContext}. HRESULT: 0x{(int)hr:X8}");
                            _reusableStagingTexture = null;
                            _suppressGpuUntil = DateTime.UtcNow.AddSeconds(2);
                            return null;
                        }

                        _reusableStagingTexture = stagingTexture;
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, $"Failed to create/resize reusable staging texture for {captureContext}.");
                        _reusableStagingTexture = null;
                        return null;
                    }
                }
                currentStagingTexture = _reusableStagingTexture;
            }

            if (currentStagingTexture == null || device == null)
            {
                _log.Error($"Staging texture or device is null after lock for {captureContext}.");
                return null;
            }

            try
            {
                device->GetImmediateContext(&deviceContext);
                if (deviceContext == null)
                {
                    _log.Error($"ProcessTexture: Failed to get immediate context for {captureContext}.");
                    return null;
                }

                deviceContext->CopyResource((ID3D11Resource*)currentStagingTexture, (ID3D11Resource*)texture);

                var hr = deviceContext->Map((ID3D11Resource*)currentStagingTexture, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
                if (hr < 0)
                {
                    _log.Warning($"ProcessTexture: Failed to map staging texture for {captureContext}. HRESULT: 0x{(int)hr:X8}");
                    lock (_stagingTextureLock)
                    {
                        if (_reusableStagingTexture != null)
                        {
                            _reusableStagingTexture->Release();
                            _reusableStagingTexture = null;
                        }
                    }
                    _suppressGpuUntil = DateTime.UtcNow.AddSeconds(2);
                    return null;
                }

                isMapped = true;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, $@"DirectX error detected during {captureContext} texture processing. Suppressing GPU work briefly and resetting staging texture.");
                lock (_stagingTextureLock)
                {
                    if (_reusableStagingTexture != null)
                    {
                        _reusableStagingTexture->Release();
                        _reusableStagingTexture = null;
                    }
                }
                _suppressGpuUntil = DateTime.UtcNow.AddSeconds(2);
                return null;
            }

            if (mapped.pData == null) return null;

            int width = checked((int)desc.Width);
            int height = checked((int)desc.Height);
            int stride = checked((int)mapped.RowPitch);
            int expectedBytesPerRow = checked(width * 4);
            int bufferSize = checked(height * stride);

            if (stride < expectedBytesPerRow || bufferSize <= 0)
            {
                deviceContext->Unmap((ID3D11Resource*)currentStagingTexture, 0);
                isMapped = false;
                return null;
            }

            rentedBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            System.Runtime.InteropServices.Marshal.Copy((nint)mapped.pData, rentedBuffer, 0, bufferSize);
            deviceContext->Unmap((ID3D11Resource*)currentStagingTexture, 0);
            isMapped = false;

            Image<Bgra32> imageSharpImage;
            int actualPixelDataSize = checked(width * height * 4);

            if (stride == expectedBytesPerRow)
            {
                imageSharpImage = Image.LoadPixelData<Bgra32>(rentedBuffer.AsSpan(0, actualPixelDataSize), width, height);
            }
            else
            {
                rentedTightBuffer = ArrayPool<byte>.Shared.Rent(actualPixelDataSize);
                var tightBufferSpan = rentedTightBuffer.AsSpan(0, actualPixelDataSize);
                for (int y = 0; y < height; y++)
                {
                    rentedBuffer.AsSpan(y * stride, expectedBytesPerRow).CopyTo(tightBufferSpan.Slice(y * expectedBytesPerRow, expectedBytesPerRow));
                }
                imageSharpImage = Image.LoadPixelData<Bgra32>(tightBufferSpan, width, height);
            }
            return imageSharpImage;
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"General Error processing {captureContext} texture.");
            return null;
        }
        finally
        {
            if (isMapped && currentStagingTexture != null && deviceContext != null)
            {
                try { deviceContext->Unmap((ID3D11Resource*)currentStagingTexture, 0); }
                catch (Exception unmapEx) { _log.Warning(unmapEx, "Exception during final UnmapSubresource."); }
            }
            if (rentedBuffer != null) ArrayPool<byte>.Shared.Return(rentedBuffer);
            if (rentedTightBuffer != null) ArrayPool<byte>.Shared.Return(rentedTightBuffer);

            if (deviceContext != null)
                deviceContext->Release();
        }
    }

    

    public Image<Bgra32>? CreateCompositedAdventurerPlateImageFromIcons(Texture* portraitTexture, ushort bannerFrame, ushort bannerDecoration)
    {
        try
        {
            _log.Debug($"CreateCompositedAdventurerPlateImageFromIcons: Loading icons for BannerFrame {bannerFrame}, BannerDecoration {bannerDecoration}");
            
            var portraitImage = GetAdventurerPlateImage(portraitTexture);
            if (portraitImage == null)
            {
                _log.Warning("CreateCompositedAdventurerPlateImageFromIcons: Failed to get portrait image");
                return null;
            }

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

            portraitImage.Dispose();

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
