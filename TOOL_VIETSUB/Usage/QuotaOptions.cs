namespace TOOL_VIETSUB.Usage;

public sealed class QuotaOptions
{
    public const string SectionName = "Quota";

    public int ReservationLifetimeMinutes { get; init; } = 120;
}
