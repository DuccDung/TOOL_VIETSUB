namespace TOOL_VIETSUB.Registration;

public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string Host { get; init; } = string.Empty;

    public int Port { get; init; } = 587;

    public bool UseStartTls { get; init; } = true;

    public string User { get; init; } = string.Empty;

    public string Pass { get; init; } = string.Empty;

    public int TimeoutSeconds { get; init; } = 30;

    public string FromName { get; init; } = "TOOL VIETSUB";
}
