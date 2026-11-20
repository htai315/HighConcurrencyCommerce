using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;

namespace HighConcurrencyCommerce.Infrastructure.Payments;

public sealed class PayOsOptions
{
    public bool Enabled { get; set; }
    public string ClientId { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ChecksumKey { get; set; } = "";
    public string ReturnUrl { get; set; } = "";
    public string CancelUrl { get; set; } = "";
    public int RequestTimeoutSeconds { get; set; } = 10;
    public int AttemptResolutionDelaySeconds { get; set; } = 15;
    public string[] CheckoutUrlAllowedHosts { get; set; } = ["pay.payos.vn"];
}

public sealed class PayOsOptionsValidator(IHostEnvironment environment) : IValidateOptions<PayOsOptions>
{
    public ValidateOptionsResult Validate(string? name, PayOsOptions options)
    {
        if (!options.Enabled) return ValidateOptionsResult.Success;

        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.ClientId)) failures.Add("PayOs:ClientId is required when enabled.");
        if (string.IsNullOrWhiteSpace(options.ApiKey)) failures.Add("PayOs:ApiKey is required when enabled.");
        if (string.IsNullOrWhiteSpace(options.ChecksumKey)) failures.Add("PayOs:ChecksumKey is required when enabled.");
        if (!IsAbsoluteHttpUrl(options.ReturnUrl)) failures.Add("PayOs:ReturnUrl must be an absolute HTTP(S) URL.");
        if (!IsAbsoluteHttpUrl(options.CancelUrl)) failures.Add("PayOs:CancelUrl must be an absolute HTTP(S) URL.");
        if (environment.IsProduction() && (!IsHttpsUrl(options.ReturnUrl) || !IsHttpsUrl(options.CancelUrl)))
            failures.Add("PayOs callback URLs must use HTTPS in Production.");
        if (options.RequestTimeoutSeconds is < 2 or > 60) failures.Add("PayOs:RequestTimeoutSeconds must be between 2 and 60.");
        if (options.AttemptResolutionDelaySeconds is < 5 or > 120)
            failures.Add("PayOs:AttemptResolutionDelaySeconds must be between 5 and 120.");
        if (options.CheckoutUrlAllowedHosts.Length == 0 || options.CheckoutUrlAllowedHosts.Any(string.IsNullOrWhiteSpace))
            failures.Add("PayOs:CheckoutUrlAllowedHosts must contain at least one valid host.");
        else if (options.CheckoutUrlAllowedHosts.Any(host => Uri.CheckHostName(host) == UriHostNameType.Unknown))
            failures.Add("PayOs:CheckoutUrlAllowedHosts contains an invalid host.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsAbsoluteHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https";

    private static bool IsHttpsUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}

