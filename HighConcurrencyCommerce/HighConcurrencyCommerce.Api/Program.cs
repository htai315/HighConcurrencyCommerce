using HighConcurrencyCommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using HighConcurrencyCommerce.Application.Catalog;
using HighConcurrencyCommerce.Infrastructure.Catalog;
using HighConcurrencyCommerce.Api.Catalog;
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
using HighConcurrencyCommerce.Application.ProductRevisions;
using HighConcurrencyCommerce.Infrastructure.ProductRevisions;
using HighConcurrencyCommerce.Api.ProductRevisions;
using HighConcurrencyCommerce.Application.ProductLifecycle;
using HighConcurrencyCommerce.Infrastructure.ProductLifecycle;
using HighConcurrencyCommerce.Api.ProductLifecycle;
using HighConcurrencyCommerce.Application.CarrierTracking;
using HighConcurrencyCommerce.Infrastructure.CarrierTracking;
using HighConcurrencyCommerce.Api.CarrierTracking;
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
using HighConcurrencyCommerce.Application.AdminAccounts;
using HighConcurrencyCommerce.Infrastructure.AdminAccounts;
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
using HighConcurrencyCommerce.Application.Reviews;
using HighConcurrencyCommerce.Infrastructure.Reviews;
using HighConcurrencyCommerce.Api.Reviews;
using HighConcurrencyCommerce.Application.Wishlist;
using HighConcurrencyCommerce.Infrastructure.Wishlist;
using HighConcurrencyCommerce.Api.Wishlist;
using HighConcurrencyCommerce.Application.Vouchers;
using HighConcurrencyCommerce.Infrastructure.Vouchers;
using HighConcurrencyCommerce.Api.Vouchers;
using HighConcurrencyCommerce.Application.Returns;
using HighConcurrencyCommerce.Infrastructure.Returns;
using HighConcurrencyCommerce.Infrastructure.Migrations;
using HighConcurrencyCommerce.Api.Returns;
using HighConcurrencyCommerce.Api.Operations;
using HighConcurrencyCommerce.Application.Categories;
using HighConcurrencyCommerce.Infrastructure.Categories;
using HighConcurrencyCommerce.Api.Categories;
using HighConcurrencyCommerce.Application.Brands;
using HighConcurrencyCommerce.Infrastructure.Brands;
using HighConcurrencyCommerce.Api.Brands;
using HighConcurrencyCommerce.Application.Shipping;
using HighConcurrencyCommerce.Infrastructure.Shipping;
using HighConcurrencyCommerce.Api.Shipping;
using HighConcurrencyCommerce.Application.Payments;
using HighConcurrencyCommerce.Infrastructure.Payments;
using HighConcurrencyCommerce.Api.Payments;
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
builder.Services.AddScoped<CsrfFilter>();
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;
    context.ProblemDetails.Extensions.TryAdd("code", context.ProblemDetails.Status == 404 ? "NOT_FOUND" : "SERVER_ERROR");
});
builder.Services.AddScoped<ICatalogReader, PostgresCatalogReader>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<GoogleAuthOptions>(builder.Configuration.GetSection("GoogleAuth"));
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<SessionCookie>();
builder.Services.AddSingleton<ChallengeCookie>();
builder.Services.AddScoped<IGoogleTokenVerifier, GoogleTokenVerifier>();
builder.Services.AddScoped<IAuthService, PostgresAuthService>();
builder.Services.Configure<AuthEmailOptions>(builder.Configuration.GetSection("AuthEmail"));
builder.Services.AddScoped<IAuthEmailSender, SmtpAuthEmailSender>();
builder.Services.AddScoped<IAuthRecoveryService, PostgresAuthRecoveryService>();
builder.Services.AddScoped<IAdminAccountService, PostgresAdminAccountService>();
builder.Services.AddHostedService<AuthEmailDeliveryWorker>();
builder.Services.AddScoped<AdminBootstrap>();
builder.Services.AddScoped<IAccountService, PostgresAccountService>();
builder.Services.AddScoped<IProductManagementService, PostgresProductManagementService>();
builder.Services.AddScoped<IProductRevisionService, PostgresProductRevisionService>();
builder.Services.AddScoped<IProductRevisionReviewReader, PostgresProductRevisionReviewReader>();
builder.Services.AddSingleton<IProductRevisionApprovalCheckpoint, NoOpProductRevisionApprovalCheckpoint>();
builder.Services.AddHostedService<ProductMediaCleanupWorker>();
builder.Services.AddScoped<IProductLifecycleService, PostgresProductLifecycleService>();
builder.Services.Configure<ProductMediaOptions>(builder.Configuration.GetSection("ProductMedia"));
builder.Services.AddSingleton<IProductMediaStorage, LocalProductMediaStorage>();
builder.Services.Configure<CarrierLabelOptions>(builder.Configuration.GetSection("CarrierLabels"));
builder.Services.AddSingleton<ICarrierLabelStorage, LocalCarrierLabelStorage>();
builder.Services.AddScoped<ICarrierAdminService, PostgresCarrierAdminService>();
builder.Services.AddScoped<ICarrierConsignmentService, PostgresCarrierConsignmentService>();
builder.Services.AddScoped<ICustomerCarrierTrackingService, PostgresCustomerCarrierTrackingService>();
builder.Services.AddScoped<IInventoryService, PostgresInventoryService>();
builder.Services.AddScoped<ICartService, PostgresCartService>();
builder.Services.AddScoped<ICheckoutService, PostgresCheckoutService>();
builder.Services.AddOptions<PayOsOptions>()
    .Bind(builder.Configuration.GetSection("PayOs"))
    .ValidateOnStart();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<PayOsOptions>, PayOsOptionsValidator>();
builder.Services.AddSingleton<IPayOsGateway, PayOsGateway>();
builder.Services.AddScoped<IPayOsPaymentService, PostgresPayOsPaymentService>();
builder.Services.AddScoped<IPayOsPaymentTransitionService, PostgresPayOsPaymentTransitionService>();
builder.Services.AddScoped<IPayOsLifecycleService, PostgresPayOsLifecycleService>();
builder.Services.AddScoped<IPayOsWebhookService, PostgresPayOsWebhookService>();
builder.Services.AddScoped<IPayOsReconciliationService, PostgresPayOsReconciliationService>();
builder.Services.AddSingleton<PayOsReconciliationMetrics>();
builder.Services.AddScoped<ICodOrderService, PostgresCodOrderService>();
builder.Services.AddScoped<IFulfillmentService, PostgresFulfillmentService>();
builder.Services.AddScoped<INotificationService, PostgresNotificationService>();
builder.Services.Configure<ReportingOptions>(builder.Configuration.GetSection("Reporting"));
builder.Services.AddScoped<IReportingService, PostgresReportingService>();
builder.Services.AddScoped<ICustomerOrderService, PostgresCustomerOrderService>();
builder.Services.AddScoped<IFlashSaleService, PostgresFlashSaleService>();
builder.Services.AddScoped<IProductReviewService, PostgresProductReviewService>();
builder.Services.AddScoped<IWishlistService, PostgresWishlistService>();
builder.Services.AddScoped<IWishlistAlertService, PostgresWishlistAlertService>();
builder.Services.AddSingleton<IProductAlertEvaluationCheckpoint, NoOpProductAlertEvaluationCheckpoint>();
builder.Services.AddHostedService<ProductAlertEvaluationWorker>();
builder.Services.AddSingleton<ICommerceEmailSender, SmtpCommerceEmailSender>();
builder.Services.AddHostedService<CommerceEmailDeliveryWorker>();
builder.Services.AddScoped<IVoucherAdminService, PostgresVoucherAdminService>();
builder.Services.AddScoped<IVoucherCustomerService, PostgresVoucherCustomerService>();
builder.Services.AddScoped<IVoucherLifecycleManager, PostgresVoucherLifecycleManager>();
builder.Services.AddSingleton<IRefundAllocationCalculator, RefundAllocationCalculator>();
builder.Services.AddScoped<IOrderReturnCustomerService, PostgresOrderReturnCustomerService>();
builder.Services.AddScoped<IOrderReturnAdminService, PostgresOrderReturnAdminService>();
builder.Services.AddScoped<IOrderReturnStaffService, PostgresOrderReturnStaffService>();
builder.Services.AddScoped<IExchangeFulfillmentService, PostgresExchangeFulfillmentService>();
builder.Services.AddScoped<ICategoryManagementService, PostgresCategoryManagementService>();
builder.Services.AddScoped<IBrandManagementService, PostgresBrandManagementService>();
builder.Services.AddSingleton<IShippingFeeCalculator, ShippingFeeCalculator>();
builder.Services.AddSingleton<IProvinceKeyNormalizer, ProvinceKeyNormalizer>();
builder.Services.AddScoped<IShippingQuoteService, PostgresShippingQuoteService>();
builder.Services.AddScoped<IShippingAdminService, PostgresShippingAdminService>();
builder.Services.AddHostedService<ExchangeAllocationExpiryWorker>();
builder.Services.AddHostedService<FlashSaleLifecycleWorker>();
builder.Services.AddMetrics();
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddCheck<PostgresReadinessHealthCheck>("postgresql", tags: ["ready"]);
builder.Services.AddHostedService<CodExpiryWorker>();
builder.Services.AddHostedService<PayOsExpiryWorker>();
builder.Services.AddExceptionHandler<AuthExceptionHandler>();
builder.Services.AddExceptionHandler<AuthRecoveryExceptionHandler>();
builder.Services.AddExceptionHandler<AdminAccountExceptionHandler>();
builder.Services.AddExceptionHandler<AccountExceptionHandler>();
builder.Services.AddExceptionHandler<ProductManagementExceptionHandler>();
builder.Services.AddExceptionHandler<ProductRevisionExceptionHandler>();
builder.Services.AddExceptionHandler<ProductLifecycleExceptionHandler>();
builder.Services.AddExceptionHandler<InventoryExceptionHandler>();
builder.Services.AddExceptionHandler<CartExceptionHandler>();
builder.Services.AddExceptionHandler<CheckoutExceptionHandler>();
builder.Services.AddExceptionHandler<PayOsPaymentExceptionHandler>();
builder.Services.AddExceptionHandler<CodOrderExceptionHandler>();
builder.Services.AddExceptionHandler<FulfillmentExceptionHandler>();
builder.Services.AddExceptionHandler<NotificationExceptionHandler>();
builder.Services.AddExceptionHandler<ReportingExceptionHandler>();
builder.Services.AddExceptionHandler<CustomerOrderExceptionHandler>();
builder.Services.AddExceptionHandler<FlashSaleExceptionHandler>();
builder.Services.AddExceptionHandler<ProductReviewExceptionHandler>();
builder.Services.AddExceptionHandler<WishlistExceptionHandler>();
builder.Services.AddExceptionHandler<VoucherExceptionHandler>();
builder.Services.AddExceptionHandler<OrderReturnExceptionHandler>();
builder.Services.AddExceptionHandler<CategoryManagementExceptionHandler>();
builder.Services.AddExceptionHandler<BrandManagementExceptionHandler>();
builder.Services.AddExceptionHandler<ShippingExceptionHandler>();
builder.Services.AddExceptionHandler<CarrierTrackingExceptionHandler>();
builder.Services.AddExceptionHandler<CatalogExceptionHandler>();
builder.Services.AddAuthentication(SessionAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    options.AddPolicy("Customer", policy => policy.RequireRole("CUSTOMER"));
    options.AddPolicy("Staff", policy => policy.RequireRole("STAFF"));
    options.AddPolicy("Admin", policy => policy.RequireRole("ADMIN"));
    options.AddPolicy("ReviewModerator", policy => policy.RequireRole("STAFF", "ADMIN"));
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
        ("password", 5, TimeSpan.FromMinutes(1)),
        ("password-reset-request", builder.Configuration.GetValue("Auth:PasswordResetPermitLimit", 5), TimeSpan.FromHours(1)),
        ("email-verification-resend", builder.Configuration.GetValue("Auth:EmailVerificationResendPermitLimit", 3), TimeSpan.FromHours(1)),
        ("auth-recovery-confirm", builder.Configuration.GetValue("Auth:AuthRecoveryConfirmPermitLimit", 10), TimeSpan.FromMinutes(15)),
        ("staff-invitation-accept", builder.Configuration.GetValue("Auth:StaffInvitationAcceptPermitLimit", 10), TimeSpan.FromMinutes(15)),
        ("shipping-quote", builder.Configuration.GetValue("Shipping:QuotePermitLimit", 30), TimeSpan.FromMinutes(1)),
        ("catalog-suggestions", builder.Configuration.GetValue("CatalogSearch:SuggestionPermitLimit", 60), TimeSpan.FromMinutes(1)) })
        options.AddPolicy(name, context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = window, QueueLimit = 0 }));
    options.AddPolicy("payos-webhook", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("PayOs:WebhookPermitLimit", 120),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});
builder.Services.AddOpenApi();

// Kết nối PostgreSQL
var connectionString = builder.Configuration
    .GetConnectionString("StoreDb");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("ConnectionStrings:StoreDb must be configured through a secret source.");

builder.Services.AddDbContext<StoreDbContext>(options =>
    options.UseNpgsql(connectionString));
builder.Services.AddSingleton(serviceProvider => new DatabaseMigrationRunner(
    connectionString,
    EmbeddedDatabaseMigrations.Load(),
    serviceProvider.GetRequiredService<ILogger<DatabaseMigrationRunner>>(),
    TimeSpan.FromSeconds(builder.Configuration.GetValue("DatabaseMigrations:LockTimeoutSeconds", 120))));

var app = builder.Build();
var maintenance = args.FirstOrDefault(arg => arg is
    "--migrate-database" or "--migrate-auth" or "--bootstrap-admin" or "--seed-demo");
if (maintenance is not null)
{
    if (maintenance == "--migrate-database")
    {
        var result = await app.Services.GetRequiredService<DatabaseMigrationRunner>().RunAsync();
        Console.WriteLine(
            $"Database migrations hoàn tất: applied={result.AppliedCount}, baselined={result.BaselineCount}, alreadyApplied={result.AlreadyAppliedCount}.");
    }
    else
    {
        if (maintenance is "--seed-demo" or "--bootstrap-admin")
            await app.Services.GetRequiredService<DatabaseMigrationRunner>().ValidateAsync();
        await AuthMaintenance.RunAsync(app.Services, maintenance);
    }
    await app.DisposeAsync();
    return;
}
if (builder.Configuration.GetValue("DatabaseMigrations:ValidateOnStartup", true))
    await app.Services.GetRequiredService<DatabaseMigrationRunner>().ValidateAsync();
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
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
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
