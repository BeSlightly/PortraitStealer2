using System;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace PortraitStealer;

public readonly unsafe struct StolenPortraitInfo
{
    public readonly ExportedPortraitData PortraitData;
    public readonly Texture* AdventurerPlateTexture;
    public readonly ushort BannerFrame;
    public readonly ushort BannerDecoration;
    public readonly byte ClassJobId;
    public readonly string ClassJobAbbreviation;
    public readonly string? ImagePath;
    public readonly DateTime Timestamp;
    public readonly string? PlayerName;
    public readonly bool IsAdventurerPlate => AdventurerPlateTexture != null;

    public StolenPortraitInfo(
        ExportedPortraitData data,
        ushort frame,
        ushort decoration,
        byte jobId,
        string jobAbbr,
        Texture* adventurerPlateTexture = null,
        string? playerName = null
    )
    {
        PortraitData = data;
        BannerFrame = frame;
        BannerDecoration = decoration;
        ClassJobId = jobId;
        ClassJobAbbreviation = jobAbbr;
        AdventurerPlateTexture = adventurerPlateTexture;
        ImagePath = null;
        Timestamp = DateTime.Now;
        PlayerName = playerName;
    }

    private StolenPortraitInfo(StolenPortraitInfo original, string imagePath)
    {
        PortraitData = original.PortraitData;
        AdventurerPlateTexture = original.AdventurerPlateTexture;
        BannerFrame = original.BannerFrame;
        BannerDecoration = original.BannerDecoration;
        ClassJobId = original.ClassJobId;
        ClassJobAbbreviation = original.ClassJobAbbreviation;
        ImagePath = imagePath;
        Timestamp = original.Timestamp;
        PlayerName = original.PlayerName;
    }

    public StolenPortraitInfo WithImagePath(string imagePath)
    {
        return new StolenPortraitInfo(this, imagePath);
    }
}
