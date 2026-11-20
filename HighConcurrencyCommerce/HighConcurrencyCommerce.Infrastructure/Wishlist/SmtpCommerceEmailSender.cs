using System.Net;
using System.Net.Mail;
using HighConcurrencyCommerce.Infrastructure.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HighConcurrencyCommerce.Infrastructure.Wishlist;

public sealed record CommerceAlertEmail(
    string RecipientEmail,
    string Subject,
    string TemplateKey,
    string ProductTitle,
    decimal? CurrentMinPrice,
    string ActionUrl);

public interface ICommerceEmailSender
{
    Task SendAsync(CommerceAlertEmail email, CancellationToken ct = default);
}

public sealed class SmtpCommerceEmailSender(
    IOptions<AuthEmailOptions> options,
    ILogger<SmtpCommerceEmailSender> logger) : ICommerceEmailSender
{
    private readonly AuthEmailOptions opt = options.Value;

    public async Task SendAsync(CommerceAlertEmail email, CancellationToken ct = default)
    {
        var heading = email.TemplateKey switch
        {
            "WISHLIST_BACK_IN_STOCK" => "Sản phẩm yêu thích đã có hàng",
            "WISHLIST_PRICE_DROP" => "Sản phẩm yêu thích vừa giảm giá",
            _ => throw new InvalidOperationException("Unsupported commerce email template.")
        };
        var detail = email.TemplateKey == "WISHLIST_PRICE_DROP" && email.CurrentMinPrice.HasValue
            ? $"Giá hiện tại từ {email.CurrentMinPrice.Value:N0} VNĐ."
            : "Sản phẩm hiện đã có hàng trở lại.";
        var safeHeading = WebUtility.HtmlEncode(heading);
        var safeTitle = WebUtility.HtmlEncode(email.ProductTitle);
        var safeDetail = WebUtility.HtmlEncode(detail);
        var safeUrl = WebUtility.HtmlEncode(email.ActionUrl);
        var subject = RemoveHeaderBreaks(email.Subject);
        var text = $"{heading}\n\n{email.ProductTitle}\n{detail}\n\nXem sản phẩm: {email.ActionUrl}\n\nBạn nhận email này vì đã bật cảnh báo trong danh sách yêu thích.";
        var html = $$"""
            <div style="font-family:Arial,sans-serif;max-width:600px;margin:auto;padding:24px;border:1px solid #e5e7eb;border-radius:12px">
              <h2 style="color:#0f2a52">{{safeHeading}}</h2>
              <p style="color:#334155"><strong>{{safeTitle}}</strong></p>
              <p style="color:#475569">{{safeDetail}}</p>
              <p style="margin:24px 0"><a href="{{safeUrl}}" style="background:#175cd3;color:#fff;padding:12px 20px;border-radius:8px;text-decoration:none">Xem sản phẩm</a></p>
              <p style="color:#64748b;font-size:12px">Bạn nhận email này vì đã bật cảnh báo trong danh sách yêu thích.</p>
            </div>
            """;

        using var client = new SmtpClient(opt.SmtpHost, opt.SmtpPort) { EnableSsl = opt.EnableSsl };
        if (!string.IsNullOrWhiteSpace(opt.SmtpUser) && !string.IsNullOrWhiteSpace(opt.SmtpPass))
            client.Credentials = new NetworkCredential(opt.SmtpUser, opt.SmtpPass);
        using var message = new MailMessage
        {
            From = new MailAddress(opt.SenderAddress, opt.SenderDisplayName),
            Subject = subject,
            Body = html,
            IsBodyHtml = true
        };
        message.To.Add(email.RecipientEmail);
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(text, null, "text/plain"));
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(html, null, "text/html"));
        try
        {
            await client.SendMailAsync(message, ct);
            logger.LogInformation("Commerce alert email {Template} sent to {Recipient}.", email.TemplateKey, MaskEmail(email.RecipientEmail));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Commerce alert email {Template} failed for {Recipient}.", email.TemplateKey, MaskEmail(email.RecipientEmail));
            throw;
        }
    }

    private static string RemoveHeaderBreaks(string value) => value.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);

    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0) return "***";
        var local = email[..at];
        return $"{local[0]}***{(local.Length > 1 ? local[^1] : '*')}{email[at..]}";
    }
}
