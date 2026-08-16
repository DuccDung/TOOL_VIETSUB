using System.Net;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Utils;

namespace SubVid.Server.Registration;

public sealed class SmtpRegistrationEmailSender(
    IOptions<SmtpOptions> options,
    IWebHostEnvironment environment)
    : IRegistrationEmailSender
{
    private readonly SmtpOptions _options = options.Value;
    private readonly string _logoPath = Path.Combine(
        environment.WebRootPath,
        "images",
        "subvid-logo.png");

    public async Task SendOtpAsync(
        string recipientEmail,
        string displayName,
        string otp,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken)
    {
        await SendAsync(
            recipientEmail,
            $"{otp} là mã xác nhận SubVid của bạn",
            $"Xin chào {displayName},\n\nMã xác nhận SubVid của bạn là: {otp}\nMã có hiệu lực trong 5 phút.\n\nNếu bạn không thực hiện đăng ký, hãy bỏ qua email này.",
            BuildHtml(
                WebUtility.HtmlEncode(displayName),
                WebUtility.HtmlEncode(otp),
                expiresAtUtc,
                "XÁC NHẬN ĐĂNG KÝ",
                "Hoàn tất tài khoản SubVid",
                "Nhập mã bên dưới để xác nhận email và bắt đầu sử dụng tài khoản của bạn.",
                "Nếu bạn không thực hiện đăng ký, hãy bỏ qua email này."),
            cancellationToken);
    }

    public async Task SendPasswordResetOtpAsync(
        string recipientEmail,
        string displayName,
        string otp,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken)
    {
        await SendAsync(
            recipientEmail,
            $"{otp} là mã đặt lại mật khẩu SubVid",
            $"Xin chào {displayName},\n\nMã đặt lại mật khẩu SubVid của bạn là: {otp}\nMã có hiệu lực trong 5 phút.\n\nNếu bạn không yêu cầu đổi mật khẩu, hãy bỏ qua email này.",
            BuildHtml(
                WebUtility.HtmlEncode(displayName),
                WebUtility.HtmlEncode(otp),
                expiresAtUtc,
                "KHÔI PHỤC TÀI KHOẢN",
                "Đặt lại mật khẩu an toàn",
                "Nhập mã bên dưới tại trang khôi phục để tạo mật khẩu mới.",
                "Nếu bạn không yêu cầu đổi mật khẩu, hãy bỏ qua email này và không chia sẻ mã OTP."),
            cancellationToken);
    }

    private async Task SendAsync(
        string recipientEmail,
        string subject,
        string textBody,
        string htmlBody,
        CancellationToken cancellationToken)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromName, _options.User));
        message.To.Add(MailboxAddress.Parse(recipientEmail));
        message.Subject = subject;
        var bodyBuilder = new BodyBuilder
        {
            TextBody = textBody,
        };
        if (File.Exists(_logoPath))
        {
            var logo = bodyBuilder.LinkedResources.Add(_logoPath);
            logo.ContentId = MimeUtils.GenerateMessageId();
            htmlBody = htmlBody.Replace(
                "%%SUBVID_LOGO%%",
                $"<img src=\"cid:{logo.ContentId}\" width=\"64\" height=\"48\" alt=\"SubVid\" style=\"display:inline-block;width:64px;height:48px;object-fit:contain;vertical-align:middle\" />",
                StringComparison.Ordinal);
        }
        else
        {
            htmlBody = htmlBody.Replace("%%SUBVID_LOGO%%", "SubVid", StringComparison.Ordinal);
        }

        bodyBuilder.HtmlBody = htmlBody;
        message.Body = bodyBuilder.ToMessageBody();

        using var client = new SmtpClient
        {
            Timeout = checked(_options.TimeoutSeconds * 1000),
        };
        var socketOptions = _options.UseStartTls
            ? SecureSocketOptions.StartTls
            : SecureSocketOptions.Auto;
        await client.ConnectAsync(
            _options.Host,
            _options.Port,
            socketOptions,
            cancellationToken);
        await client.AuthenticateAsync(_options.User, _options.Pass, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    private static string BuildHtml(
        string displayName,
        string otp,
        DateTime expiresAtUtc,
        string eyebrow,
        string heading,
        string description,
        string safetyNote) => $$"""
        <!doctype html>
        <html lang="vi">
        <body style="margin:0;background:#070b13;color:#eef5ff;font-family:Segoe UI,Arial,sans-serif">
          <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background:#070b13;padding:32px 16px">
            <tr><td align="center">
              <table role="presentation" width="520" cellspacing="0" cellpadding="0" style="max-width:520px;border:1px solid #263850;border-radius:16px;background:#0d1624;overflow:hidden">
                <tr><td style="padding:10px 24px;border-bottom:1px solid #1d2b3e;color:#93c5fd;font-size:12px;font-weight:700;letter-spacing:.08em">%%SUBVID_LOGO%% <span style="margin-left:8px;color:#61738b;font-size:9px">AI VIDEO STUDIO</span></td></tr>
                <tr><td style="padding:30px 32px">
                  <div style="color:#67e8f9;font-size:10px;font-weight:700;letter-spacing:.14em">{{eyebrow}}</div>
                  <h1 style="margin:10px 0 8px;color:#f4f8fd;font-size:24px">{{heading}}</h1>
                  <p style="margin:0 0 5px;color:#dbeafe;font-size:13px;line-height:1.7">Xin chào {{displayName}},</p>
                  <p style="margin:0;color:#8fa1b8;font-size:13px;line-height:1.7">{{description}}</p>
                  <div style="margin:26px 0;border:1px solid #315f96;border-radius:12px;background:#102b4d;padding:20px;text-align:center;color:#dbeafe;font-family:Consolas,monospace;font-size:34px;font-weight:700;letter-spacing:12px">{{otp}}</div>
                  <p style="margin:0;color:#7890ac;font-size:11px;line-height:1.7">Mã có hiệu lực trong <strong style="color:#bfdbfe">5 phút</strong> và chỉ sử dụng một lần. Hết hạn lúc {{expiresAtUtc:HH:mm:ss}} UTC.</p>
                  <div style="margin-top:22px;border-top:1px solid #1d2b3e;padding-top:18px;color:#5f7189;font-size:10px">{{safetyNote}}</div>
                </td></tr>
                <tr><td style="padding:14px 24px;background:#09111c;color:#52647b;font-size:9px">SubVid · Vietnamese AI Video Studio</td></tr>
              </table>
            </td></tr>
          </table>
        </body>
        </html>
        """;
}
