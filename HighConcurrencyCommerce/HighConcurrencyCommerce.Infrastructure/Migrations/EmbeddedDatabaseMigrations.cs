using System.Reflection;

namespace HighConcurrencyCommerce.Infrastructure.Migrations;

public static class EmbeddedDatabaseMigrations
{
    private static readonly (string Version, string Description, string ResourceName)[] Manifest =
    [
        ("001", "Create core commerce schema", "Database.Migrations.001_create_schema.sql"),
        ("002", "Add authentication sessions", "Database.Migrations.002_auth_sessions.sql"),
        ("003", "Add Flash Sale cart offer snapshot", "Database.Migrations.003_flash_sale_cart_offer.sql"),
        ("004", "Add Google external identities", "Database.Migrations.004_google_auth.sql"),
        ("005", "Add email verification and account recovery", "Database.Migrations.005_auth_email_recovery.sql"),
        ("006", "Add internal Staff invitations", "Database.Migrations.006_internal_staff_invites.sql"),
        ("008", "Add product reviews", "Database.Migrations.008_product_reviews.sql"),
        ("009", "Add customer wishlist", "Database.Migrations.009_customer_wishlist.sql"),
        ("010", "Add basic vouchers", "Database.Migrations.010_basic_vouchers.sql"),
        ("011", "Add COD returns and manual refunds", "Database.Migrations.011_cod_returns_refunds.sql"),
        ("012", "Add returned-goods inspection and quarantine", "Database.Migrations.012_return_quality_inspection.sql"),
        ("013", "Add direct product exchange and replacement shipment", "Database.Migrations.013_direct_product_exchange.sql"),
        ("014", "Add brands and product specifications", "Database.Migrations.014_brand_product_specifications.sql"),
        ("015", "Add product revision workflow", "Database.Migrations.015_product_revisions.sql"),
        ("016", "Add product lifecycle management", "Database.Migrations.016_product_lifecycle.sql"),
        ("017", "Harden product revision review and media cleanup", "Database.Migrations.017_product_revision_hardening.sql"),
        ("018", "Add shipping methods and order shipping fees", "Database.Migrations.018_shipping_methods_fees.sql"),
        ("019", "Add carrier consignments and tracking timeline", "Database.Migrations.019_carrier_consignments_tracking.sql"),
        ("020", "Add PostgreSQL-native catalog search indexes", "Database.Migrations.020_advanced_product_search.sql"),
        ("021", "Add Wishlist product alerts and commerce outbox", "Database.Migrations.021_wishlist_product_alerts.sql"),
        ("022", "Harden PayOS payment attempts and callback idempotency", "Database.Migrations.022_payos_integration_hardening.sql"),
        ("023", "Add PayOS reconciliation assignment workflow", "Database.Migrations.023_payos_reconciliation_workflow.sql"),
        ("024", "Harden PayOS reconciliation transaction linkage", "Database.Migrations.024_payos_reconciliation_hardening.sql")
    ];

    public static IReadOnlyList<DatabaseMigration> Load()
    {
        var assembly = typeof(EmbeddedDatabaseMigrations).Assembly;
        return Manifest.Select(item => DatabaseMigration.Create(
            item.Version,
            item.Description,
            ReadResource(assembly, item.ResourceName))).ToArray();
    }

    private static string ReadResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new DatabaseMigrationException($"Không tìm thấy embedded migration '{resourceName}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

