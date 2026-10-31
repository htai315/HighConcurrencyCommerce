using HighConcurrencyCommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using HighConcurrencyCommerce.Application.Catalog;
using HighConcurrencyCommerce.Infrastructure.Catalog;
using Microsoft.AspNetCore.Mvc;
using HighConcurrencyCommerce.Application.Auth;
using HighConcurrencyCommerce.Infrastructure.Auth;
using HighConcurrencyCommerce.Api.Auth;
using HighConcurrencyCommerce.Application.Account;
using HighConcurrencyCommerce.Infrastructure.Account;
using HighConcurrencyCommerce.Api.Account;
using HighConcurrencyCommerce.Application.ProductManagement;
using HighConcurrencyCommerce.Infrastructure.ProductManagement;
using HighConcurrencyCommerce.Api.ProductManagement;
using HighConcurrencyCommerce.Application.Inventory;
using HighConcurrencyCommerce.Infrastructure.Inventory;
using HighConcurrencyCommerce.Api.Inventory;
using HighConcurrencyCommerce.Application.Cart;
using HighConcurrencyCommerce.Infrastructure.Cart;
using HighConcurrencyCommerce.Api.Cart;
using HighConcurrencyCommerce.Application.Checkout;
using HighConcurrencyCommerce.Infrastructure.Checkout;
using HighConcurrencyCommerce.Api.Checkout;
using HighConcurrencyCommerce.Application.Orders;
using HighConcurrencyCommerce.Infrastructure.Orders;
using HighConcurrencyCommerce.Api.Orders;
using HighConcurrencyCommerce.Application.Fulfillment;
using HighConcurrencyCommerce.Infrastructure.Fulfillment;
using HighConcurrencyCommerce.Api.Fulfillment;
using HighConcurrencyCommerce.Application.Notifications;
using HighConcurrencyCommerce.Infrastructure.Notifications;
using HighConcurrencyCommerce.Api.Notifications;
using HighConcurrencyCommerce.Application.Reporting;
using HighConcurrencyCommerce.Infrastructure.Reporting;
using HighConcurrencyCommerce.Api.Reporting;
using HighConcurrencyCommerce.Application.CustomerOrders;
using HighConcurrencyCommerce.Infrastructure.CustomerOrders;
using HighConcurrencyCommerce.Api.CustomerOrders;
using HighConcurrencyCommerce.Application.FlashSale;
using HighConcurrencyCommerce.Infrastructure.FlashSale;
using HighConcurrencyCommerce.Api.FlashSale;
using HighConcurrencyCommerce.Api.Operations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var requireHttps = builder.Configuration.GetValue("Security:RequireHttps", !builder.Environment.IsDevelopment());

builder.Logging.Configure(options => options.ActivityTrackingOptions =
    ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId | ActivityTrackingOptions.ParentId);
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    var configuredProxies = builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [];
    if (configuredProxies.Length == 0) return;
    options.KnownProxies.Clear();
    foreach (var value in configuredProxies)
        options.KnownProxies.Add(IPAddress.TryParse(value, out var address)
            ? address
            : throw new InvalidOperationException($"ReverseProxy:KnownProxies contains invalid IP address '{value}'."));
});

var dataProtection = builder.Services.AddDataProtection().SetApplicationName("HighConcurrencyCommerce");
var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));

builder.Services.AddControllers(options => options.Filters.Add<CsrfFilter>()).ConfigureApiBehaviorOptions(options =>
    options.InvalidModelStateResponseFactory = context =>
    {
        var problem = new ValidationProblemDetails(context.ModelState)
        {
            Status = 400,
            Title = "Dữ liệu không hợp lệ."
        };
        problem.Extensions["code"] = "VALIDATION_ERROR";
        problem.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;
        return new BadRequestObjectResult(problem);
    });
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;
    context.ProblemDetails.Extensions.TryAdd("code", context.ProblemDetails.Status == 404 ? "NOT_FOUND" : "SERVER_ERROR");
});
builder.Services.AddScoped<ICatalogReader, PostgresCatalogReader>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<SessionCookie>();
builder.Services.AddScoped<IAuthService, PostgresAuthService>();
builder.Services.AddScoped<AdminBootstrap>();
builder.Services.AddScoped<IAccountService, PostgresAccountService>();
builder.Services.AddScoped<IProductManagementService, PostgresProductManagementService>();
builder.Services.AddScoped<IInventoryService, PostgresInventoryService>();
builder.Services.AddScoped<ICartService, PostgresCartService>();
builder.Services.AddScoped<ICheckoutService, PostgresCheckoutService>();
builder.Services.AddScoped<ICodOrderService, PostgresCodOrderService>();
builder.Services.AddScoped<IFulfillmentService, PostgresFulfillmentService>();
builder.Services.AddScoped<INotificationService, PostgresNotificationService>();
builder.Services.Configure<ReportingOptions>(builder.Configuration.GetSection("Reporting"));
builder.Services.AddScoped<IReportingService, PostgresReportingService>();
builder.Services.AddScoped<ICustomerOrderService, PostgresCustomerOrderService>();
builder.Services.AddScoped<IFlashSaleService, PostgresFlashSaleService>();
builder.Services.AddHostedService<FlashSaleLifecycleWorker>();
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddCheck<PostgresReadinessHealthCheck>("postgresql", tags: ["ready"]);
builder.Services.AddHostedService<CodExpiryWorker>();
builder.Services.AddExceptionHandler<AuthExceptionHandler>();
builder.Services.AddExceptionHandler<AccountExceptionHandler>();
builder.Services.AddExceptionHandler<ProductManagementExceptionHandler>();
builder.Services.AddExceptionHandler<InventoryExceptionHandler>();
builder.Services.AddExceptionHandler<CartExceptionHandler>();
builder.Services.AddExceptionHandler<CheckoutExceptionHandler>();
builder.Services.AddExceptionHandler<CodOrderExceptionHandler>();
builder.Services.AddExceptionHandler<FulfillmentExceptionHandler>();
builder.Services.AddExceptionHandler<NotificationExceptionHandler>();
builder.Services.AddExceptionHandler<ReportingExceptionHandler>();
builder.Services.AddExceptionHandler<CustomerOrderExceptionHandler>();
builder.Services.AddExceptionHandler<FlashSaleExceptionHandler>();
builder.Services.AddAuthentication(SessionAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    options.AddPolicy("Customer", policy => policy.RequireRole("CUSTOMER"));
    options.AddPolicy("Staff", policy => policy.RequireRole("STAFF"));
    options.AddPolicy("Admin", policy => policy.RequireRole("ADMIN"));
});
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = requireHttps ? "__Host-hcc-csrf" : "hcc-csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.Path = "/";
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = requireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.None;
});
builder.Services.AddRateLimiter(options =>
{
    options.OnRejected = async (context, _) =>
    {
        var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)) : 60;
        context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await AuthProblems.Write(context.HttpContext, 429, "RATE_LIMITED", "Bạn thao tác quá nhanh. Vui lòng thử lại sau.");
    };
    foreach (var (name, limit, window) in new[] {
        ("login", builder.Configuration.GetValue("Auth:LoginPermitLimit", 10), TimeSpan.FromMinutes(1)),
        ("register", builder.Configuration.GetValue("Auth:RegisterPermitLimit", 5), TimeSpan.FromHours(1)),
        ("password", 5, TimeSpan.FromMinutes(1)) })
        options.AddPolicy(name, context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = window, QueueLimit = 0 }));
});
builder.Services.AddOpenApi();

// Kết nối PostgreSQL
var connectionString = builder.Configuration
    .GetConnectionString("StoreDb");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("ConnectionStrings:StoreDb must be configured through a secret source.");

builder.Services.AddDbContext<StoreDbContext>(options =>
    options.UseNpgsql(connectionString));

var app = builder.Build();
var maintenance = args.FirstOrDefault(arg => arg is "--migrate-auth" or "--bootstrap-admin");
if (maintenance is not null)
{
    await AuthMaintenance.RunAsync(app.Services, maintenance);
    await app.DisposeAsync();
    return;
}
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseForwardedHeaders();
app.UseMiddleware<CorrelationIdMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

if (requireHttps) app.UseHttpsRedirection();
app.UseRouting();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/v1/auth") || context.Request.Path.StartsWithSegments("/api/v1/admin") || context.Request.Path.StartsWithSegments("/api/v1/account") || context.Request.Path.StartsWithSegments("/api/v1/staff") || context.Request.Path.StartsWithSegments("/api/v1/cart") || context.Request.Path.StartsWithSegments("/api/v1/checkouts") || context.Request.Path.StartsWithSegments("/api/v1/notifications") || context.Request.Path.StartsWithSegments("/api/v1/orders"))
        context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live"),
    ResponseWriter = HealthResponseWriter.WriteAsync
}).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
    ResponseWriter = HealthResponseWriter.WriteAsync
}).AllowAnonymous();

app.Run();

public partial class Program { }
