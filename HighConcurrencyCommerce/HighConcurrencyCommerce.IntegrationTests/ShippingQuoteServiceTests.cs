using HighConcurrencyCommerce.Application.Shipping;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class ShippingQuoteServiceTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private static readonly Guid CustomerId = Guid.Parse("81000000-0000-0000-0000-000000000001");
    private static readonly Guid AddressId = Guid.Parse("81000000-0000-0000-0000-000000000002");
    private static readonly Guid ExpressId = Guid.Parse("81000000-0000-0000-0000-000000000003");
    private static readonly Guid StandardId = Guid.Parse("81000000-0000-0000-0000-000000000004");
    private static readonly Guid OfferId = Guid.Parse("81000000-0000-0000-0000-000000000005");
    private static readonly Guid SkuId = Guid.Parse("30000000-0000-0000-0000-000000000002");

    [Fact]
    public async Task Quote_is_owner_scoped_deterministic_and_uses_flash_sale_subtotal_before_voucher()
    {
        await SeedAsync();
        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IShippingQuoteService>();

        var regular = await service.QuoteAsync(CustomerId, Request());

        Assert.Equal(AddressId, regular.ShippingAddressId);
        Assert.Equal("HO CHI MINH", regular.DestinationProvinceKey);
        Assert.Equal(3_500_000m, regular.MerchandiseSubtotal);
        Assert.Collection(regular.Quotes,
            express =>
            {
                Assert.Equal(ExpressId, express.ShippingMethodId);
                Assert.True(express.IsDefault);
                Assert.Equal(55_000m, express.ShippingFee);
                Assert.Equal(5_000m, express.ProvinceSurcharge);
            },
            standard =>
            {
                Assert.Equal(StandardId, standard.ShippingMethodId);
                Assert.True(standard.FreeShippingApplied);
                Assert.Equal(0m, standard.ShippingFee);
                Assert.Equal(10_000m, standard.ProvinceSurcharge);
            });

        var flashSale = await service.QuoteAsync(CustomerId, Request(OfferId));

        Assert.Equal(3_000_000m, flashSale.MerchandiseSubtotal);
        var standardFlashQuote = Assert.Single(flashSale.Quotes, x => x.ShippingMethodId == StandardId);
        Assert.False(standardFlashQuote.FreeShippingApplied);
        Assert.Equal(30_000m, standardFlashQuote.ShippingFee);
    }

    [Fact]
    public async Task Quote_hides_address_existence_from_other_customers()
    {
        await SeedAsync();
        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IShippingQuoteService>();

        var failure = await Assert.ThrowsAsync<ShippingFailure>(() =>
            service.QuoteAsync(Guid.NewGuid(), Request()));

        Assert.Equal(404, failure.Status);
        Assert.Equal("ADDRESS_NOT_FOUND", failure.Code);
    }

    private static ShippingQuoteRequest Request(Guid? offerId = null) => new()
    {
        ShippingAddressId = AddressId,
        Lines =
        [
            new ShippingQuoteLineRequest
            {
                ProductSkuId = SkuId,
                Quantity = 1,
                FlashSaleOfferId = offerId
            }
        ]
    };

    private async Task SeedAsync()
    {
        await using var connection = await fixture.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE store.shipping_methods
            SET is_default=false, method_status='INACTIVE'
            WHERE id='90000000-0000-0000-0000-000000000001';

            INSERT INTO store.user_accounts(id, role_id, email, password_hash, full_name, account_status, email_verified_at)
            VALUES (
                @customer_id,
                (SELECT id FROM store.roles WHERE role_key = 'CUSTOMER'),
                'shipping-quote@example.invalid', 'x', 'Shipping Quote', 'ACTIVE', now())
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO store.customer_addresses(id, customer_id, recipient_name, phone, address_line, province, is_default)
            VALUES (@address_id, @customer_id, 'Shipping Quote', '0900000010', '10 Quote', 'Thành phố Hồ Chí Minh', true)
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO store.shipping_methods(
                id, code, name, method_status, base_fee, free_shipping_threshold,
                estimated_min_days, estimated_max_days, is_default, display_order, revision, created_by, updated_by)
            VALUES
                (@express_id, 'EXPRESS', 'Giao nhanh', 'ACTIVE', 50000, NULL, 1, 2, true, 99, 3,
                    '00000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000002'),
                (@standard_id, 'STANDARD', 'Giao tiêu chuẩn', 'ACTIVE', 20000, 3500000, 3, 5, false, 1, 7,
                    '00000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000002')
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO store.shipping_method_province_rates(
                shipping_method_id, province_key, surcharge_fee, is_active, created_by, updated_by)
            VALUES
                (@express_id, 'HO CHI MINH', 5000, true,
                    '00000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000002'),
                (@standard_id, 'HO CHI MINH', 10000, true,
                    '00000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000002')
            ON CONFLICT (shipping_method_id, province_key) DO NOTHING;

            INSERT INTO store.flash_sale_campaigns(id, name, starts_at, ends_at, campaign_status, created_by)
            VALUES (@campaign_id, 'Shipping quote flash sale', now() - interval '1 hour', now() + interval '1 hour', 'ACTIVE',
                    '00000000-0000-0000-0000-000000000002')
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO store.flash_sale_offers(
                id, campaign_id, product_sku_id, sale_price, quota_total, quota_held, quota_sold, per_customer_limit)
            VALUES (@offer_id, @campaign_id, @sku_id, 3000000, 10, 0, 0, 2)
            ON CONFLICT (id) DO NOTHING;
            """, connection);
        command.Parameters.AddWithValue("customer_id", CustomerId);
        command.Parameters.AddWithValue("address_id", AddressId);
        command.Parameters.AddWithValue("express_id", ExpressId);
        command.Parameters.AddWithValue("standard_id", StandardId);
        command.Parameters.AddWithValue("campaign_id", Guid.Parse("81000000-0000-0000-0000-000000000006"));
        command.Parameters.AddWithValue("offer_id", OfferId);
        command.Parameters.AddWithValue("sku_id", SkuId);
        await command.ExecuteNonQueryAsync();
    }
}
