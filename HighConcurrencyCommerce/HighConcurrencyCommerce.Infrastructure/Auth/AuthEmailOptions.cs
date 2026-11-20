namespace HighConcurrencyCommerce.Infrastructure.Auth;

public sealed class AuthEmailOptions
{
    public string SmtpHost { get; set; } = "localhost";
    public int SmtpPort { get; set; } = 1025;
    public string SenderAddress { get; set; } = "no-reply@hcc.local";
    public string SenderDisplayName { get; set; } = "HighConcurrencyCommerce";
    public string FrontendBaseUrl { get; set; } = "http://localhost:5173";
    public bool EnableSsl { get; set; } = false;
    public string? SmtpUser { get; set; }
    public string? SmtpPass { get; set; }
}

