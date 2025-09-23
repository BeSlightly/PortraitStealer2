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
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;
using Texture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace PortraitStealer.Services;

public unsafe class PortraitCaptureService : IDisposable
{
    private readonly IPluginLog _log;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IGameGui _gameGui;
    private readonly IDataManager _dataManager;
    private readonly ITextureProvider _textureProvider;
    private readonly Device? _device;
    private DateTime _suppressGpuUntil = DateTime.MinValue;

    private Texture2D? _reusableStagingTexture;
    private Texture2DDescription _reusableStagingDesc;
    private object _stagingTextureLock = new();

    public PortraitCaptureService(IPluginLog log, IDalamudPluginInterface pluginInterface, IGameGui gameGui, IDataManager dataManager, ITextureProvider textureProvider)
    {
        _log = log;
        _pluginInterface = pluginInterface;
        _gameGui = gameGui;
        _dataManager = dataManager;
        _textureProvider = textureProvider;
        try
        {
            _device = (Device?)_pluginInterface.UiBuilder.DeviceHandle;
            if (_device == null)
                _log.Error("Failed to get D3D11 device. Portrait capture will not work.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Exception getting Device from UiBuilder.Device.");
            _device = null;
        }
    }

    public void Dispose()
    {
        lock (_stagingTextureLock)
        {
            _reusableStagingTexture?.Dispose();
            _reusableStagingTexture = null;
        }
    }

    public Image<Bgra32>? GetAdventurerPlateImage(Texture* portraitTexture)
    {
        if (_device == null) return null;
        if (portraitTexture == null || portraitTexture->D3D11Texture2D == null) return null;

        var texture = CppObject.FromPointer<Texture2D>((nint)portraitTexture->D3D11Texture2D);
        if (texture == null) return null;

        return ProcessTexture(texture, "Adventurer Plate");
    }

    public Image<Bgra32>? GetDutyPortraitImage(int partySlotIndex)
    {
        if (_device == null) return null;
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

        var texture = CppObject.FromPointer<Texture2D>((nint)charaViewTexture->D3D11Texture2D);
        if (texture == null) return null;

        return ProcessTexture(texture, $"Duty Portrait (Slot {partySlotIndex + 1})");
    }

    private Image<Bgra32>? ProcessTexture(Texture2D texture, string context)
    {
        byte[]? rentedBuffer = null;
        byte[]? rentedTightBuffer = null;
        DataBox dataBox = default;
        Texture2D? currentStagingTexture = null;

        try
        {
            if (DateTime.UtcNow < _suppressGpuUntil)
            {
                _log.Debug($"ProcessTexture: Suppressing GPU work during cooldown for {context}.");
                return null;
            }

            var desc = texture.Description;
            
            if (desc.Format == Format.BC7_UNorm)
            {
                _log.Warning($@"Unsupported compressed format for {context}: {desc.Format}");
                return null;
            }
            
            if (desc.Format != Format.B8G8R8A8_UNorm && desc.Format != Format.R8G8B8A8_UNorm)
            {
                _log.Warning($"ProcessTexture: {context} texture has unsupported format ({desc.Format})");
                return null;
            }

            lock (_stagingTextureLock)
            {
                if (_reusableStagingTexture == null || _reusableStagingDesc.Width != desc.Width || _reusableStagingDesc.Height != desc.Height || _reusableStagingDesc.Format != desc.Format)
                {
                    _reusableStagingTexture?.Dispose();
                    _reusableStagingDesc = new Texture2DDescription
                    {
                        Width = desc.Width,
                        Height = desc.Height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = desc.Format,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Staging,
                        BindFlags = BindFlags.None,
                        CpuAccessFlags = CpuAccessFlags.Read,
                        OptionFlags = ResourceOptionFlags.None,
                    };
                    try
                    {
                        _reusableStagingTexture = new Texture2D(_device, _reusableStagingDesc);
                    }
                    catch (SharpDXException dxEx) when (dxEx.ResultCode == SharpDX.DXGI.ResultCode.DeviceRemoved || dxEx.ResultCode == SharpDX.DXGI.ResultCode.DeviceReset)
                    {
                        _log.Error(dxEx, $"Failed to create/resize reusable staging texture for {context} due to device lost/reset.");
                        _reusableStagingTexture = null;
                        _suppressGpuUntil = DateTime.UtcNow.AddSeconds(2);
                        return null;
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, $"Failed to create/resize reusable staging texture for {context}.");
                        _reusableStagingTexture = null;
                        return null;
                    }
                }
                currentStagingTexture = _reusableStagingTexture;
            }

            if (currentStagingTexture == null || _device == null)
            {
                _log.Error($"Staging texture or device is null after lock for {context}.");
                return null;
            }

            try
            {
                _device.ImmediateContext.CopyResource(texture, currentStagingTexture);
                dataBox = _device.ImmediateContext.MapSubresource(currentStagingTexture, 0, MapMode.Read, MapFlags.None);
            }
            catch (SharpDXException dxEx) when (dxEx.ResultCode == SharpDX.DXGI.ResultCode.DeviceRemoved || dxEx.ResultCode == SharpDX.DXGI.ResultCode.DeviceReset)
            {
                _log.Warning(dxEx, $@"Device lost/reset detected during {context} texture processing. Suppressing GPU work briefly and resetting staging texture.");
                lock (_stagingTextureLock)
                {
                    _reusableStagingTexture?.Dispose();
                    _reusableStagingTexture = null;
                }
                _suppressGpuUntil = DateTime.UtcNow.AddSeconds(2);
                return null;
            }

            if (dataBox.DataPointer == IntPtr.Zero) return null;

            int stride = dataBox.RowPitch;
            int expectedBytesPerRow = desc.Width * 4;
            int bufferSize = desc.Height * stride;

            if (stride < expectedBytesPerRow || bufferSize <= 0)
            {
                _device.ImmediateContext.UnmapSubresource(currentStagingTexture, 0);
                dataBox.DataPointer = IntPtr.Zero;
                return null;
            }

            rentedBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            System.Runtime.InteropServices.Marshal.Copy(dataBox.DataPointer, rentedBuffer, 0, bufferSize);
            _device.ImmediateContext.UnmapSubresource(currentStagingTexture, 0);
            dataBox.DataPointer = IntPtr.Zero;

            Image<Bgra32> imageSharpImage;
            int actualPixelDataSize = desc.Width * desc.Height * 4;

            if (stride == expectedBytesPerRow)
            {
                imageSharpImage = Image.LoadPixelData<Bgra32>(rentedBuffer.AsSpan(0, actualPixelDataSize), desc.Width, desc.Height);
            }
            else
            {
                rentedTightBuffer = ArrayPool<byte>.Shared.Rent(actualPixelDataSize);
                var tightBufferSpan = rentedTightBuffer.AsSpan(0, actualPixelDataSize);
                for (int y = 0; y < desc.Height; y++)
                {
                    rentedBuffer.AsSpan(y * stride, expectedBytesPerRow).CopyTo(tightBufferSpan.Slice(y * expectedBytesPerRow, expectedBytesPerRow));
                }
                imageSharpImage = Image.LoadPixelData<Bgra32>(tightBufferSpan, desc.Width, desc.Height);
            }
            return imageSharpImage;
        }
        catch (SharpDXException dxEx)
        {
            _log.Error(dxEx, $"DirectX Error processing {context} texture. ResultCode: {dxEx.ResultCode}");
            return null;
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"General Error processing {context} texture.");
            return null;
        }
        finally
        {
            if (dataBox.DataPointer != IntPtr.Zero && currentStagingTexture != null)
            {
                try { _device?.ImmediateContext.UnmapSubresource(currentStagingTexture, 0); }
                catch (Exception unmapEx) { _log.Warning(unmapEx, "Exception during final UnmapSubresource."); }
            }
            if (rentedBuffer != null) ArrayPool<byte>.Shared.Return(rentedBuffer);
            if (rentedTightBuffer != null) ArrayPool<byte>.Shared.Return(rentedTightBuffer);
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
