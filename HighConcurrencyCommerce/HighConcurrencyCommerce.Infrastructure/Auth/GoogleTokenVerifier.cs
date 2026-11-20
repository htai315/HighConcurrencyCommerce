using Google.Apis.Auth;
using HighConcurrencyCommerce.Application.Auth;
using Microsoft.Extensions.Options;

namespace HighConcurrencyCommerce.Infrastructure.Auth;

public sealed class GoogleTokenVerifier(IOptions<GoogleAuthOptions> options) : IGoogleTokenVerifier
{
    public async Task<GoogleUserPayload> ValidateAsync(string idToken, string expectedNonce, CancellationToken ct)
    {
        var clientId = options.Value.ClientId;
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("Google ClientId is not configured.");

        if (string.IsNullOrWhiteSpace(idToken))
            throw new AuthFailure("GOOGLE_AUTH_INVALID");

        var settings = new GoogleJsonWebSignature.ValidationSettings
        {
            Audience = [clientId]
        };

        GoogleJsonWebSignature.Payload payload;
        try
        {
            payload = await GoogleJsonWebSignature.ValidateAsync(idToken, settings);
        }
        catch (InvalidJwtException)
        {
            throw new AuthFailure("GOOGLE_AUTH_INVALID");
        }
        catch (ArgumentException)
        {
            throw new AuthFailure("GOOGLE_AUTH_INVALID");
        }
        catch (FormatException)
        {
            throw new AuthFailure("GOOGLE_AUTH_INVALID");
        }
        catch (Exception ex) when (ex is not AuthFailure)
        {
            throw new AuthFailure("GOOGLE_AUTH_UNAVAILABLE");
        }

        if (string.IsNullOrWhiteSpace(payload.Subject))
            throw new AuthFailure("GOOGLE_AUTH_INVALID");

        if (string.IsNullOrWhiteSpace(payload.Email) || !payload.EmailVerified)
            throw new AuthFailure("GOOGLE_AUTH_INVALID");

        if (!string.Equals(payload.Nonce, expectedNonce, StringComparison.Ordinal))
            throw new AuthFailure("GOOGLE_AUTH_INVALID");

        return new GoogleUserPayload(
            payload.Subject,
            payload.Email.Trim().ToLowerInvariant(),
            payload.EmailVerified,
            payload.Name,
            payload.GivenName,
            payload.FamilyName,
            payload.Nonce
        );
    }
}
