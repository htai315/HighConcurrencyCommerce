using System.Security.Claims;
using System.Text.Encodings.Web;
using HighConcurrencyCommerce.Application.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace HighConcurrencyCommerce.Api.Auth;

public sealed class SessionCookie(IHostEnvironment environment, IConfiguration configuration)
{
    private bool RequireHttps => configuration.GetValue("Security:RequireHttps", !environment.IsDevelopment());
    public string Name => RequireHttps ? "__Host-hcc-session" : "hcc-session";
    private CookieOptions Options(DateTime? expiry = null) => new()
    {
        HttpOnly = true, Secure = RequireHttps, SameSite = SameSiteMode.Lax,
        Path = "/", Expires = expiry is null ? null : new DateTimeOffset(expiry.Value)
    };
    public string? Read(HttpContext context) => context.Request.Cookies[Name];
    public void Write(HttpContext context, SessionTicket ticket) => context.Response.Cookies.Append(Name, ticket.Token, Options(ticket.ExpiresAt));
    public void Delete(HttpContext context) => context.Response.Cookies.Delete(Name, Options());
}

public sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IAuthService auth, SessionCookie cookie) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Session";
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = cookie.Read(Context);
        if (token is null) return AuthenticateResult.NoResult();
        var user = await auth.FindSessionAsync(token, Context.RequestAborted);
        if (user is null) return AuthenticateResult.Fail("Session invalid or expired.");
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.FullName),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role)
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        AuthProblems.Write(Context, 401, "UNAUTHORIZED", "Vui lòng đăng nhập.");
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        AuthProblems.Write(Context, 403, "FORBIDDEN", "Bạn không có quyền truy cập.");
}

