namespace TOOL_VIETSUB.Registration;

public sealed class RegistrationOptions
{
    public const string SectionName = "Registration";

    public string OtpSecret { get; init; } = string.Empty;

    public int OtpLifetimeMinutes { get; init; } = 5;

    public int ResendCooldownSeconds { get; init; } = 60;

    public int MaxAttempts { get; init; } = 5;

    public int MaxResends { get; init; } = 3;
}
