namespace HighConcurrencyCommerce.Api.Auth;

public sealed class ChallengeCookie(IHostEnvironment environment, IConfiguration configuration)
{
    private bool RequireHttps => configuration.GetValue("Security:RequireHttps", !environment.IsDevelopment());
    public string Name => RequireHttps ? "__Host-hcc-challenge" : "hcc-challenge";
    private CookieOptions Options(DateTime? expiry = null) => new()
    {
        HttpOnly = true, Secure = RequireHttps, SameSite = SameSiteMode.Lax,
        Path = "/", Expires = expiry is null ? null : new DateTimeOffset(expiry.Value)
    };
    public string? Read(HttpContext context) => context.Request.Cookies[Name];
    public void Write(HttpContext context, string token, DateTime expiry) => context.Response.Cookies.Append(Name, token, Options(expiry));
    public void Delete(HttpContext context) => context.Response.Cookies.Delete(Name, Options());
}

