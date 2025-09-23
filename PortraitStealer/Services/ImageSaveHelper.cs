using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace PortraitStealer.Services;

public static class ImageSaveHelper
{
    public static async Task ProcessImageSaveInBackgroundAsync(
        IPluginLog log,
        string tempFolder,
        uint objectId,
        string imagePath,
        byte[] pixelData,
        int width,
        int height,
        SemaphoreSlim semaphore
    )
    {
        await semaphore.WaitAsync();
        try
        {
            var directory = Path.GetDirectoryName(imagePath);
            if (directory != null && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var bgImage = Image.LoadPixelData<Bgra32>(pixelData, width, height);
            bgImage.SaveAsPng(
                imagePath,
                new PngEncoder { CompressionLevel = PngCompressionLevel.BestSpeed }
            );
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[ImageSaveHelper] Failed to save temporary image: {imagePath}");
            if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
            {
                try
                {
                    File.Delete(imagePath);
                }
                catch { }
            }
        }
        finally
        {
            semaphore.Release();
        }
    }
}
