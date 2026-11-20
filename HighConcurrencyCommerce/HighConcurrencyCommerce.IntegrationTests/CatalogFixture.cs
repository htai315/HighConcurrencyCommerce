using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using HighConcurrencyCommerce.Application.Payments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HighConcurrencyCommerce.IntegrationTests;

// A disposable PostgreSQL database per fixture. Never executes schema or seed in StoreDb.
public sealed class CatalogFixture : IAsyncLifetime
{
    private readonly string databaseName = "hcc_tests_" + Guid.NewGuid().ToString("N");
    private string adminConnection = "";
    private bool databaseCreated;
    private WebApplicationFactory<Program>? factory;
    private string testConnection = "";
    private readonly string mediaRoot = Path.Combine(Path.GetTempPath(), "hcc-media-tests", Guid.NewGuid().ToString("N"));
    private readonly string carrierLabelRoot = Path.Combine(Path.GetTempPath(), "hcc-carrier-label-tests", Guid.NewGuid().ToString("N"));
    public HttpClient Client { get; private set; } = null!;
    public IServiceProvider Services => factory!.Services;

    public HttpClient CreateClient() => factory!.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
    });

    public WebApplicationFactory<Program> CreateRateLimitedApplication() => new CatalogApiFactory(testConnection, mediaRoot, carrierLabelRoot, true);

    public WebApplicationFactory<Program> CreateConcurrentApplication()
    {
        var settings = new NpgsqlConnectionStringBuilder(testConnection)
        {
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = 32
        };
        return new CatalogApiFactory(settings.ConnectionString, mediaRoot, carrierLabelRoot);
    }

    public WebApplicationFactory<Program> CreatePayOsApplication(IPayOsGateway gateway, bool enabled = true) =>
        new CatalogApiFactory(testConnection, mediaRoot, carrierLabelRoot, false, gateway, enabled);

    public async Task<NpgsqlConnection> OpenDatabaseAsync()
    {
        var connection = new NpgsqlConnection(testConnection);
        await connection.OpenAsync();
        return connection;
    }

    public async Task VerifyUserAsync(string email)
    {
        await using var connection = await OpenDatabaseAsync();
        await using var cmd = new NpgsqlCommand("UPDATE store.user_accounts SET email_verified_at = now() WHERE lower(email) = lower(@email)", connection);
        cmd.Parameters.AddWithValue("email", email);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder().AddUserSecrets<Program>().Build();
        var connection = Environment.GetEnvironmentVariable("HCC_TEST_POSTGRES")
            ?? configuration.GetConnectionString("StoreDb")
            ?? throw new InvalidOperationException("Set HCC_TEST_POSTGRES or API User Secrets ConnectionStrings:StoreDb for a LOCAL PostgreSQL role with CREATEDB.");
        var settings = new NpgsqlConnectionStringBuilder(connection);
        if (settings.Host is not ("localhost" or "127.0.0.1" or "::1"))
            throw new InvalidOperationException("Integration tests only allow loopback PostgreSQL hosts.");

        settings.Database = "postgres";
        settings.Pooling = false;
        adminConnection = settings.ConnectionString;
        await using (var admin = new NpgsqlConnection(adminConnection))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
            databaseCreated = true;
        }

        try
        {
            settings.Database = databaseName;
            testConnection = settings.ConnectionString;
            await using var db = new NpgsqlConnection(settings.ConnectionString);
            await db.OpenAsync();
            foreach (var resource in new[] { "Schema.sql", "CatalogSeed.sql", "AuthSchema.sql", "FlashSaleCartSchema.sql", "GoogleAuthSchema.sql", "AuthEmailRecoverySchema.sql", "InternalStaffInvitesSchema.sql", "ProductReviewsSchema.sql", "CustomerWishlistSchema.sql", "BasicVouchersSchema.sql", "CodReturnsRefundsSchema.sql", "ReturnQualityInspectionSchema.sql", "DirectProductExchangeSchema.sql", "BrandProductSpecificationsSchema.sql", "ProductRevisionsSchema.sql", "ProductLifecycleSchema.sql", "ProductRevisionHardeningSchema.sql", "ShippingMethodsFeesSchema.sql", "CarrierConsignmentsTrackingSchema.sql", "AdvancedProductSearchSchema.sql", "WishlistProductAlertsSchema.sql", "PayOsIntegrationHardeningSchema.sql", "PayOsReconciliationWorkflowSchema.sql", "PayOsReconciliationHardeningSchema.sql", "ShippingCheckoutSeed.sql" })
            {
                await using var stream = typeof(CatalogFixture).Assembly.GetManifestResourceStream(resource)
                    ?? throw new InvalidOperationException($"Missing embedded fixture {resource}");
                using var reader = new StreamReader(stream);
                await using var command = new NpgsqlCommand(await reader.ReadToEndAsync(), db);
                await command.ExecuteNonQueryAsync();
            }
            factory = new CatalogApiFactory(settings.ConnectionString, mediaRoot, carrierLabelRoot);
            Client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
            });
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (factory is not null) await factory.DisposeAsync();
        if (!databaseCreated) return;
        // Name generated by this fixture, not accepted from configuration or user input.
        if (!System.Text.RegularExpressions.Regex.IsMatch(databaseName, "^hcc_tests_[0-9a-f]{32}$"))
            throw new InvalidOperationException("Refusing to drop a non-fixture database.");
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
        databaseCreated = false;
        var resolvedMediaRoot = Path.GetFullPath(mediaRoot);
        var safeParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "hcc-media-tests")) + Path.DirectorySeparatorChar;
        if (resolvedMediaRoot.StartsWith(safeParent, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolvedMediaRoot))
            Directory.Delete(resolvedMediaRoot, recursive: true);
        var resolvedCarrierLabelRoot = Path.GetFullPath(carrierLabelRoot);
        var safeCarrierParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "hcc-carrier-label-tests")) + Path.DirectorySeparatorChar;
        if (resolvedCarrierLabelRoot.StartsWith(safeCarrierParent, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolvedCarrierLabelRoot))
            Directory.Delete(resolvedCarrierLabelRoot, recursive: true);
    }

    private sealed class CatalogApiFactory(
        string connection,
        string mediaRoot,
        string carrierLabelRoot,
        bool productionLimits = false,
        IPayOsGateway? payOsGateway = null,
        bool payOsEnabled = false) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:StoreDb", connection);
            builder.UseSetting("DatabaseMigrations:ValidateOnStartup", "false");
            builder.UseSetting("ProductMedia:RootPath", mediaRoot);
            builder.UseSetting("CarrierLabels:RootPath", carrierLabelRoot);
            builder.UseSetting("PayOs:Enabled", payOsEnabled.ToString());
            builder.UseSetting("PayOs:ClientId", payOsEnabled ? "integration-client" : "");
            builder.UseSetting("PayOs:ApiKey", payOsEnabled ? "integration-api-key" : "");
            builder.UseSetting("PayOs:ChecksumKey", payOsEnabled ? new string('a', 64) : "");
            builder.UseSetting("PayOs:ReturnUrl", "https://shop.test/payment/payos/return");
            builder.UseSetting("PayOs:CancelUrl", "https://shop.test/payment/payos/return?cancel=1");
            if (!productionLimits)
            {
                builder.UseSetting("Auth:LoginPermitLimit", "1000");
                builder.UseSetting("Auth:RegisterPermitLimit", "1000");
                builder.UseSetting("Auth:PasswordResetPermitLimit", "1000");
                builder.UseSetting("Auth:EmailVerificationResendPermitLimit", "1000");
                builder.UseSetting("Auth:AuthRecoveryConfirmPermitLimit", "1000");
                builder.UseSetting("Auth:StaffInvitationAcceptPermitLimit", "1000");
                builder.UseSetting("Shipping:QuotePermitLimit", "1000");
                builder.UseSetting("CatalogSearch:SuggestionPermitLimit", "1000");
            }
            builder.UseSetting("CodExpiry:PollIntervalMilliseconds", "100");
            builder.UseSetting("FlashSaleLifecycle:PollIntervalMilliseconds", "100");
            builder.ConfigureServices(services =>
            {
                if (payOsGateway is not null)
                {
                    services.RemoveAll<IPayOsGateway>();
                    services.AddSingleton(payOsGateway);
                }
                var workerDescriptor = services.SingleOrDefault(d =>
                    d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) &&
                    d.ImplementationType == typeof(HighConcurrencyCommerce.Infrastructure.Auth.AuthEmailDeliveryWorker));
                if (workerDescriptor != null) services.Remove(workerDescriptor);
                var exchangeExpiryWorker = services.SingleOrDefault(d =>
                    d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) &&
                    d.ImplementationType == typeof(HighConcurrencyCommerce.Infrastructure.Returns.ExchangeAllocationExpiryWorker));
                if (exchangeExpiryWorker != null) services.Remove(exchangeExpiryWorker);
                var payOsExpiryWorker = services.SingleOrDefault(d =>
                    d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) &&
                    d.ImplementationType == typeof(HighConcurrencyCommerce.Api.Payments.PayOsExpiryWorker));
                if (payOsExpiryWorker != null) services.Remove(payOsExpiryWorker);
                var mediaCleanupWorker = services.SingleOrDefault(d =>
                    d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) &&
                    d.ImplementationType == typeof(HighConcurrencyCommerce.Infrastructure.ProductRevisions.ProductMediaCleanupWorker));
                if (mediaCleanupWorker != null) services.Remove(mediaCleanupWorker);
                var productAlertWorker = services.SingleOrDefault(d =>
                    d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) &&
                    d.ImplementationType == typeof(HighConcurrencyCommerce.Infrastructure.Wishlist.ProductAlertEvaluationWorker));
                if (productAlertWorker != null) services.Remove(productAlertWorker);
                var commerceEmailWorker = services.SingleOrDefault(d =>
                    d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) &&
                    d.ImplementationType == typeof(HighConcurrencyCommerce.Infrastructure.Wishlist.CommerceEmailDeliveryWorker));
                if (commerceEmailWorker != null) services.Remove(commerceEmailWorker);
            });
        }
    }
}
