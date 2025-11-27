namespace PortraitStealer;

public static class MemoryOffsets
{
    public static class BannerCharacterData
    {
        // These offsets are based on the 'BannerData' struct defined in FFXIVClientStructs/FFXIV/Client/Game/UI/CharaCard.cs.
        // The game reuses this 'BannerData' layout inside the AgentBannerInterface (Party/Duty Portraits).
        //
        // To verify or recalculate:
        // 1. Look at 'BannerData' in CharaCard.cs.
        // 2. Note the relative offsets: BannerDecoration is at 0x2A, BannerFrame is at 0x2E.
        // 3. In AgentBannerInterface, this 'BannerData' block sits exactly 0x40 bytes BEFORE the 'Title' field.
        //    (Title is at 0x750, so BannerData starts at 0x710).
        // 4. 0x710 + 0x2E = 0x73E (BannerFrame)
        // 5. 0x710 + 0x2A = 0x73A (BannerDecoration)
        public const int BannerFrame = 0x73E;
        public const int BannerDecoration = 0x73A;
    }
}
