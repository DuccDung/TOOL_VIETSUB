using System.Net;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace TOOL_VIETSUB.Registration;

public sealed class SmtpRegistrationEmailSender(IOptions<SmtpOptions> options)
    : IRegistrationEmailSender
{
    private readonly SmtpOptions _options = options.Value;

    public async Task SendOtpAsync(
        string recipientEmail,
        string displayName,
        string otp,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken)
    {
        var safeName = WebUtility.HtmlEncode(displayName);
        var safeOtp = WebUtility.HtmlEncode(otp);
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromName, _options.User));
        message.To.Add(MailboxAddress.Parse(recipientEmail));
        message.Subject = $"{otp} là mã xác nhận TOOL VIETSUB của bạn";
        message.Body = new BodyBuilder
        {
            TextBody = $"Xin chào {displayName},\n\nMã xác nhận TOOL VIETSUB của bạn là: {otp}\nMã có hiệu lực trong 5 phút.\n\nNếu bạn không thực hiện đăng ký, hãy bỏ qua email này.",
            HtmlBody = BuildHtml(safeName, safeOtp, expiresAtUtc),
        }.ToMessageBody();

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

    private static string BuildHtml(string displayName, string otp, DateTime expiresAtUtc) => $$"""
        <!doctype html>
        <html lang="vi">
        <body style="margin:0;background:#070b13;color:#eef5ff;font-family:Segoe UI,Arial,sans-serif">
          <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background:#070b13;padding:32px 16px">
            <tr><td align="center">
              <table role="presentation" width="520" cellspacing="0" cellpadding="0" style="max-width:520px;border:1px solid #263850;border-radius:16px;background:#0d1624;overflow:hidden">
                <tr><td style="padding:18px 24px;border-bottom:1px solid #1d2b3e;color:#93c5fd;font-size:12px;font-weight:700;letter-spacing:.08em">▣ TOOL VIETSUB <span style="color:#61738b;font-size:9px">STUDIO</span></td></tr>
                <tr><td style="padding:30px 32px">
                  <div style="color:#60a5fa;font-size:10px;font-weight:700;letter-spacing:.14em">XÁC NHẬN ĐĂNG KÝ</div>
                  <h1 style="margin:10px 0 8px;color:#f4f8fd;font-size:24px">Xin chào {{displayName}}</h1>
                  <p style="margin:0;color:#8fa1b8;font-size:13px;line-height:1.7">Nhập mã bên dưới vào ứng dụng để hoàn tất đăng ký tài khoản.</p>
                  <div style="margin:26px 0;border:1px solid #315f96;border-radius:12px;background:#102b4d;padding:20px;text-align:center;color:#dbeafe;font-family:Consolas,monospace;font-size:34px;font-weight:700;letter-spacing:12px">{{otp}}</div>
                  <p style="margin:0;color:#7890ac;font-size:11px;line-height:1.7">Mã có hiệu lực trong <strong style="color:#bfdbfe">5 phút</strong> và chỉ sử dụng một lần. Hết hạn lúc {{expiresAtUtc:HH:mm:ss}} UTC.</p>
                  <div style="margin-top:22px;border-top:1px solid #1d2b3e;padding-top:18px;color:#5f7189;font-size:10px">Nếu bạn không thực hiện đăng ký, hãy bỏ qua email này. Không chia sẻ mã OTP cho bất kỳ ai.</div>
                </td></tr>
                <tr><td style="padding:14px 24px;background:#09111c;color:#52647b;font-size:9px">TOOL VIETSUB · Vietnamese AI Studio</td></tr>
              </table>
            </td></tr>
          </table>
        </body>
        </html>
        """;
}
