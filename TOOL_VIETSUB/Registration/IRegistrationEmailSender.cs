namespace TOOL_VIETSUB.Registration;

public interface IRegistrationEmailSender
{
    Task SendOtpAsync(
        string recipientEmail,
        string displayName,
        string otp,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken);
}
