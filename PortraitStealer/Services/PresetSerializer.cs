using System;
using System.IO;
using Dalamud.Plugin.Services;
using PortraitStealer;

namespace PortraitStealer.Services;

public class PresetSerializer
{
    private readonly IPluginLog _log;

    public PresetSerializer(IPluginLog log)
    {
        _log = log;
    }

    internal string? GeneratePresetString(StolenPortraitInfo info)
    {
        var data = info.PortraitData;

        if (!string.IsNullOrEmpty(info.PlayerName) && info.PlayerName != "Local Player")
        {
            _log.Debug($"Generating preset string for {info.PlayerName} - data may not be compatible with your character");
        }

        try
        {
            using var outputStream = new MemoryStream(128);
            using var writer = new BinaryWriter(outputStream);
            const int Magic = 0x53505448;
            const ushort Version = 1;
            writer.Write(Magic);
            writer.Write(Version);
            writer.WriteHalf(data.CameraPosition.X);
            writer.WriteHalf(data.CameraPosition.Y);
            writer.WriteHalf(data.CameraPosition.Z);
            writer.WriteHalf((Half)1.0f);
            writer.WriteHalf(data.CameraTarget.X);
            writer.WriteHalf(data.CameraTarget.Y);
            writer.WriteHalf(data.CameraTarget.Z);
            writer.WriteHalf((Half)1.0f);
            writer.Write(data.ImageRotation);
            writer.Write(data.CameraZoom);
            writer.Write(data.BannerTimeline);
            writer.Write(data.AnimationProgress);
            writer.Write(data.Expression);
            writer.WriteHalf(data.HeadDirection.X);
            writer.WriteHalf(data.HeadDirection.Y);
            writer.WriteHalf(data.EyeDirection.X);
            writer.WriteHalf(data.EyeDirection.Y);
            writer.Write(data.DirectionalLightingColorRed);
            writer.Write(data.DirectionalLightingColorGreen);
            writer.Write(data.DirectionalLightingColorBlue);
            writer.Write(data.DirectionalLightingBrightness);
            writer.Write(data.DirectionalLightingVerticalAngle);
            writer.Write(data.DirectionalLightingHorizontalAngle);
            writer.Write(data.AmbientLightingColorRed);
            writer.Write(data.AmbientLightingColorGreen);
            writer.Write(data.AmbientLightingColorBlue);
            writer.Write(data.AmbientLightingBrightness);
            writer.Write(data.BannerBg);
            writer.Write(info.BannerFrame);
            writer.Write(info.BannerDecoration);
            return Convert.ToBase64String(outputStream.ToArray());
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to generate preset string.");
            return null;
        }
    }
}
