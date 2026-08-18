namespace SubVid.Server.Cloud;

public sealed class CloudAccessOptions
{
    public const string SectionName = "CloudAccess";

    public int ReservationLifetimeMinutes { get; set; } = 45;
}

public static class CloudUsageUnits
{
    public const string LlmToken = "LLM_TOKEN";
    public const string TtsCharacter = "TTS_CHARACTER";
    public const string SttSecond = "STT_SECOND";
}
