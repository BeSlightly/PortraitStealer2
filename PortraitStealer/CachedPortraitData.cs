using System;

namespace PortraitStealer;

public enum FetchState
{
    WaitingDelay,
    Queued,
    InFlight,
    Succeeded,
    Failed
}

public struct CachedPortraitData
{
    public readonly string PlayerName;
    public readonly uint ClientObjectId;
    public readonly int SlotIndex;
    public readonly byte ClassJobId;
    public readonly string ClassJobAbbreviation;
    public readonly DateTime Timestamp;

    public FetchState State { get; private set; }
    public DateTime LastBasicUpdateTimestamp { get; private set; }
    public bool NeedsFullDataFetchAttempt { get; private set; }

    public StolenPortraitInfo? FullPortraitData { get; private set; }
    public string? TemporaryImagePath { get; private set; }

    public CachedPortraitData(
        string playerName,
        uint clientObjectId,
        int slotIndex,
        byte classJobId,
        string classJobAbbreviation,
        DateTime timestamp
    )
    {
        PlayerName = playerName ?? string.Empty;
        ClientObjectId = clientObjectId;
        SlotIndex = slotIndex;
        ClassJobId = classJobId;
        ClassJobAbbreviation = classJobAbbreviation ?? "?";
        Timestamp = timestamp;
        LastBasicUpdateTimestamp = timestamp;
        State = FetchState.WaitingDelay;
        NeedsFullDataFetchAttempt = true;
        FullPortraitData = null;
        TemporaryImagePath = null;
    }

    private CachedPortraitData(
        CachedPortraitData original,
        StolenPortraitInfo? fullData,
        string? imagePath,
        bool needsFetch,
        FetchState state,
        DateTime timestamp,
        DateTime basicTimestamp
    )
    {
        this.PlayerName = original.PlayerName;
        this.ClientObjectId = original.ClientObjectId;
        this.SlotIndex = original.SlotIndex;
        this.ClassJobId = original.ClassJobId;
        this.ClassJobAbbreviation = original.ClassJobAbbreviation;
        this.FullPortraitData = fullData;
        this.TemporaryImagePath = imagePath;
        this.NeedsFullDataFetchAttempt = needsFetch;
        this.State = state;
        this.Timestamp = timestamp;
        this.LastBasicUpdateTimestamp = basicTimestamp;
    }

    public CachedPortraitData WithFullData(StolenPortraitInfo? fetchedData, string? imagePath)
    {
        FetchState newState = (fetchedData != null) ? FetchState.Succeeded : FetchState.Failed;

        return new CachedPortraitData(
            original: this,
            fullData: fetchedData,
            imagePath: imagePath,
            needsFetch: false,
            state: newState,
            timestamp: DateTime.Now,
            basicTimestamp: this.LastBasicUpdateTimestamp
        );
    }

    public CachedPortraitData WithFetchQueued()
    {
        return new CachedPortraitData(
            original: this,
            fullData: this.FullPortraitData,
            imagePath: this.TemporaryImagePath,
            needsFetch: false,
            state: FetchState.Queued,
            timestamp: this.Timestamp,
            basicTimestamp: this.LastBasicUpdateTimestamp
        );
    }
}
