using HighConcurrencyCommerce.Application.Auth;
using HighConcurrencyCommerce.Infrastructure.Auth;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class BootstrapTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    [Fact]
    public async Task Bootstrap_creates_only_one_admin_under_concurrent_invocations()
    {
        await using var connection = await fixture.OpenDatabaseAsync();
        await using (var clearSeed = new NpgsqlCommand("UPDATE store.user_accounts SET role_id=(SELECT id FROM store.roles WHERE role_key='STAFF') WHERE role_id=(SELECT id FROM store.roles WHERE role_key='ADMIN')", connection))
            await clearSeed.ExecuteNonQueryAsync();
        var passwords = new PasswordService();
        async Task<bool> Create(string email)
        {
            await using var bootstrapConnection = await fixture.OpenDatabaseAsync();
            await using var db = new StoreDbContext(new DbContextOptionsBuilder<StoreDbContext>().UseNpgsql(bootstrapConnection).Options);
            return await new AdminBootstrap(db, passwords, TimeProvider.System).CreateAsync(
                new RegisterRequest { Email = email, FullName = "Bootstrap Admin", Password = "Bootstrap test password 123!", ConfirmPassword = "Bootstrap test password 123!" }, default);
        }
        var results = await Task.WhenAll(Create("first@example.invalid"), Create("second@example.invalid"));
        Assert.Single(results, created => created);
        await using var verifyDb = new StoreDbContext(new DbContextOptionsBuilder<StoreDbContext>().UseNpgsql(connection).Options);
        var admin = await verifyDb.UserAccounts.Include(u => u.Role).SingleAsync(u => u.Role.RoleKey == "ADMIN");
        Assert.Equal(PasswordVerificationResult.Success, passwords.Verify(admin, "Bootstrap test password 123!"));
        Assert.False(await Create("third@example.invalid"));
        Assert.Equal(1, await verifyDb.AuditLogs.CountAsync(a => a.Action == "ADMIN_BOOTSTRAPPED"));
    }
}
