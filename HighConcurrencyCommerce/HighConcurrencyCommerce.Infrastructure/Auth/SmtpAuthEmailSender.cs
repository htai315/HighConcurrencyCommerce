using System.Net.Mail;
using HighConcurrencyCommerce.Application.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HighConcurrencyCommerce.Infrastructure.Auth;

public sealed class SmtpAuthEmailSender(
    IOptions<AuthEmailOptions> options,
    ILogger<SmtpAuthEmailSender> logger) : IAuthEmailSender
{
    private readonly AuthEmailOptions opt = options.Value;

    public async Task SendPasswordResetEmailAsync(string recipientEmail, string resetLink, CancellationToken ct = default)
    {
        var subject = "[HighConcurrencyCommerce] Yêu cầu đặt lại mật khẩu";
        var bodyText = $"""
            Xin chào,

            Chúng tôi đã nhận được yêu cầu đặt lại mật khẩu cho tài khoản liên kết với địa chỉ email này.
            Vui lòng bấm vào liên kết dưới đây hoặc sao chép vào trình duyệt để tiếp tục:

            {resetLink}

            Liên kết này chỉ có hiệu lực trong vòng 15 phút.
            Nếu bạn không yêu cầu đặt lại mật khẩu, vui lòng bỏ qua email này. Tài khoản của bạn vẫn an toàn.

            Trân trọng,
            Đội ngũ HighConcurrencyCommerce
            """;

        var bodyHtml = $"""
            <div style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; max-width: 600px; margin: 0 auto; padding: 24px; border: 1px solid #e5e7eb; border-radius: 8px;">
                <h2 style="color: #111827; margin-bottom: 16px;">Yêu cầu đặt lại mật khẩu</h2>
                <p style="color: #374151; font-size: 15px; line-height: 1.5;">Chúng tôi đã nhận được yêu cầu đặt lại mật khẩu cho tài khoản liên kết với địa chỉ email này.</p>
                <div style="margin: 24px 0;">
                    <a href="{resetLink}" style="background-color: #2563eb; color: #ffffff; padding: 12px 24px; text-decoration: none; border-radius: 6px; font-weight: 500; display: inline-block;">Đặt lại mật khẩu</a>
                </div>
                <p style="color: #6b7280; font-size: 13px; line-height: 1.4;">Liên kết này có hiệu lực trong vòng <strong>15 phút</strong>. Nếu nút bấm không hoạt động, sao chép liên kết này vào trình duyệt:</p>
                <p style="color: #2563eb; font-size: 13px; word-break: break-all;">{resetLink}</p>
                <hr style="border: none; border-top: 1px solid #e5e7eb; margin: 24px 0;" />
                <p style="color: #9ca3af; font-size: 12px;">Nếu bạn không thực hiện yêu cầu này, vui lòng bỏ qua email. Tài khoản của bạn hoàn toàn được bảo vệ.</p>
            </div>
            """;

        await SendEmailAsync(recipientEmail, subject, bodyText, bodyHtml, ct);
    }

    public async Task SendVerificationEmailAsync(string recipientEmail, string verificationLink, CancellationToken ct = default)
    {
        var subject = "[HighConcurrencyCommerce] Xác minh địa chỉ email";
        var bodyText = $"""
            Xin chào,

            Cảm ơn bạn đã đăng ký tài khoản tại HighConcurrencyCommerce.
            Vui lòng bấm vào liên kết dưới đây hoặc sao chép vào trình duyệt để kích hoạt tài khoản của bạn:

            {verificationLink}

            Liên kết này chỉ có hiệu lực trong vòng 24 giờ.
            Nếu bạn không đăng ký tài khoản tại HighConcurrencyCommerce, vui lòng bỏ qua email này.

            Trân trọng,
            Đội ngũ HighConcurrencyCommerce
            """;

        var bodyHtml = $"""
            <div style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; max-width: 600px; margin: 0 auto; padding: 24px; border: 1px solid #e5e7eb; border-radius: 8px;">
                <h2 style="color: #111827; margin-bottom: 16px;">Xác minh địa chỉ email</h2>
                <p style="color: #374151; font-size: 15px; line-height: 1.5;">Cảm ơn bạn đã đăng ký tài khoản. Vui lòng bấm nút bên dưới để xác minh địa chỉ email và kích hoạt tài khoản của bạn.</p>
                <div style="margin: 24px 0;">
                    <a href="{verificationLink}" style="background-color: #16a34a; color: #ffffff; padding: 12px 24px; text-decoration: none; border-radius: 6px; font-weight: 500; display: inline-block;">Xác minh email ngay</a>
                </div>
                <p style="color: #6b7280; font-size: 13px; line-height: 1.4;">Liên kết này có hiệu lực trong vòng <strong>24 giờ</strong>. Nếu nút bấm không hoạt động, sao chép liên kết này vào trình duyệt:</p>
                <p style="color: #16a34a; font-size: 13px; word-break: break-all;">{verificationLink}</p>
                <hr style="border: none; border-top: 1px solid #e5e7eb; margin: 24px 0;" />
                <p style="color: #9ca3af; font-size: 12px;">Nếu bạn không thực hiện đăng ký tài khoản, vui lòng bỏ qua email này.</p>
            </div>
            """;

        await SendEmailAsync(recipientEmail, subject, bodyText, bodyHtml, ct);
    }

    public async Task SendStaffInvitationEmailAsync(string recipientEmail, string invitationLink, CancellationToken ct = default)
    {
        var subject = "[HighConcurrencyCommerce] Lời mời tham gia đội ngũ nhân sự (Staff)";
        var bodyText = $"""
            Xin chào,

            Bạn đã được Quản trị viên mời tham gia hệ thống HighConcurrencyCommerce với vai trò Nhân sự (Staff).
            Vui lòng truy cập liên kết dưới đây để thiết lập mật khẩu và kích hoạt tài khoản của bạn:

            {invitationLink}

            Liên kết này chỉ có hiệu lực trong vòng 24 giờ và chỉ dùng được một lần.
            Nếu bạn không mong đợi email này, vui lòng bỏ qua hoặc liên hệ với Quản trị viên của bạn.

            Trân trọng,
            Đội ngũ HighConcurrencyCommerce
            """;

        var bodyHtml = $"""
            <div style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; max-width: 600px; margin: 0 auto; padding: 24px; border: 1px solid #e5e7eb; border-radius: 8px;">
                <h2 style="color: #111827; margin-bottom: 16px;">Lời mời tham gia đội ngũ nhân sự</h2>
                <p style="color: #374151; font-size: 15px; line-height: 1.5;">Bạn đã được mời tham gia hệ thống HighConcurrencyCommerce với vai trò <strong>Nhân sự (Staff)</strong>. Vui lòng bấm vào nút bên dưới để thiết lập mật khẩu đăng nhập:</p>
                <div style="margin: 24px 0;">
                    <a href="{invitationLink}" style="background-color: #4f46e5; color: #ffffff; padding: 12px 24px; text-decoration: none; border-radius: 6px; font-weight: 500; display: inline-block;">Thiết lập mật khẩu &amp; Kích hoạt</a>
                </div>
                <p style="color: #6b7280; font-size: 13px; line-height: 1.4;">Liên kết này có hiệu lực trong vòng <strong>24 giờ</strong> và dùng được <strong>1 lần duy nhất</strong>. Nếu nút bấm không hoạt động, hãy sao chép liên kết dưới đây vào trình duyệt:</p>
                <p style="color: #4f46e5; font-size: 13px; word-break: break-all;">{invitationLink}</p>
                <hr style="border: none; border-top: 1px solid #e5e7eb; margin: 24px 0;" />
                <p style="color: #9ca3af; font-size: 12px;">Nếu bạn không mong đợi email này hoặc cho rằng có sự nhầm lẫn, vui lòng liên hệ trực tiếp Quản trị viên hệ thống.</p>
            </div>
            """;

        await SendEmailAsync(recipientEmail, subject, bodyText, bodyHtml, ct);
    }

    private async Task SendEmailAsync(string recipientEmail, string subject, string bodyText, string bodyHtml, CancellationToken ct)
    {
        using var client = new SmtpClient(opt.SmtpHost, opt.SmtpPort)
        {
            EnableSsl = opt.EnableSsl
        };
        if (!string.IsNullOrEmpty(opt.SmtpUser) && !string.IsNullOrEmpty(opt.SmtpPass))
        {
            client.Credentials = new System.Net.NetworkCredential(opt.SmtpUser, opt.SmtpPass);
        }

        using var message = new MailMessage
        {
            From = new MailAddress(opt.SenderAddress, opt.SenderDisplayName),
            Subject = subject,
            Body = bodyHtml,
            IsBodyHtml = true
        };
        message.To.Add(recipientEmail);

        var plainTextView = AlternateView.CreateAlternateViewFromString(bodyText, null, "text/plain");
        var htmlView = AlternateView.CreateAlternateViewFromString(bodyHtml, null, "text/html");
        message.AlternateViews.Add(plainTextView);
        message.AlternateViews.Add(htmlView);

        try
        {
            await client.SendMailAsync(message, ct);
            logger.LogInformation("Auth email '{Subject}' successfully sent to recipient: {Recipient}", subject, MaskEmail(recipientEmail));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send auth email '{Subject}' to recipient: {Recipient}", subject, MaskEmail(recipientEmail));
            throw;
        }
    }

    private static string MaskEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "***";
        var atIndex = email.IndexOf('@');
        if (atIndex <= 0) return "***";
        var prefix = email[..atIndex];
        var domain = email[atIndex..];
        var maskedPrefix = prefix.Length switch
        {
            1 => "*",
            2 => $"{prefix[0]}*",
            _ => $"{prefix[0]}***{prefix[^1]}"
        };
        return $"{maskedPrefix}{domain}";
    }
}

