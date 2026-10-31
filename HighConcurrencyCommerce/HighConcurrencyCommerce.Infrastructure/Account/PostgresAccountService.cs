using System.Data;
using HighConcurrencyCommerce.Application.Account;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HighConcurrencyCommerce.Infrastructure.Account;

public sealed class PostgresAccountService(StoreDbContext db) : IAccountService
{
    public async Task<CustomerProfileDto> GetProfileAsync(Guid customerId, CancellationToken ct = default)
    {
        var user = await db.UserAccounts
            .AsNoTracking()
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == customerId, ct);

        if (user is null)
        {
            throw new AccountFailure("NOT_FOUND", "Không tìm thấy tài khoản.");
        }

        return new CustomerProfileDto(user.Id, user.Email, user.FullName, user.Phone, user.Role.RoleKey);
    }

    public async Task<CustomerProfileDto> UpdateProfileAsync(Guid customerId, UpdateProfileRequest request, CancellationToken ct = default)
    {
        var user = await db.UserAccounts
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == customerId, ct);

        if (user is null)
        {
            throw new AccountFailure("NOT_FOUND", "Không tìm thấy tài khoản.");
        }

        user.FullName = request.FullName.Trim();
        user.Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();
        user.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return new CustomerProfileDto(user.Id, user.Email, user.FullName, user.Phone, user.Role.RoleKey);
    }

    public async Task<IReadOnlyList<AddressDto>> GetAddressesAsync(Guid customerId, CancellationToken ct = default)
    {
        return await db.CustomerAddresses
            .AsNoTracking()
            .Where(a => a.CustomerId == customerId)
            .OrderByDescending(a => a.IsDefault)
            .ThenByDescending(a => a.CreatedAt)
            .Select(a => new AddressDto(
                a.Id,
                a.RecipientName,
                a.Phone,
                a.AddressLine,
                a.Ward,
                a.Province,
                a.IsDefault,
                a.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<AddressDto> GetAddressAsync(Guid customerId, Guid addressId, CancellationToken ct = default)
    {
        var address = await db.CustomerAddresses
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == addressId && a.CustomerId == customerId, ct);

        if (address is null)
        {
            throw new AccountFailure("NOT_FOUND", "Không tìm thấy địa chỉ.");
        }

        return new AddressDto(
            address.Id,
            address.RecipientName,
            address.Phone,
            address.AddressLine,
            address.Ward,
            address.Province,
            address.IsDefault,
            address.CreatedAt);
    }

    public async Task<AddressDto> CreateAddressAsync(Guid customerId, CreateAddressRequest request, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var customerExists = await db.Database.SqlQueryRaw<Guid>(
            "SELECT id AS \"Value\" FROM store.user_accounts WHERE id = {0} FOR UPDATE", customerId)
            .FirstOrDefaultAsync(ct);

        if (customerExists == Guid.Empty)
        {
            throw new AccountFailure("NOT_FOUND", "Không tìm thấy tài khoản.");
        }

        var hasExistingAddresses = await db.CustomerAddresses.AnyAsync(a => a.CustomerId == customerId, ct);
        var makeDefault = request.IsDefault || !hasExistingAddresses;

        if (makeDefault)
        {
            await db.CustomerAddresses
                .Where(a => a.CustomerId == customerId && a.IsDefault)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.IsDefault, false)
                    .SetProperty(a => a.UpdatedAt, DateTime.UtcNow), ct);
        }

        var address = new CustomerAddress
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            RecipientName = request.RecipientName.Trim(),
            Phone = request.Phone.Trim(),
            AddressLine = request.AddressLine.Trim(),
            Ward = string.IsNullOrWhiteSpace(request.Ward) ? null : request.Ward.Trim(),
            Province = request.Province.Trim(),
            IsDefault = makeDefault,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.CustomerAddresses.Add(address);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new AddressDto(
            address.Id,
            address.RecipientName,
            address.Phone,
            address.AddressLine,
            address.Ward,
            address.Province,
            address.IsDefault,
            address.CreatedAt);
    }

    public async Task<AddressDto> UpdateAddressAsync(Guid customerId, Guid addressId, UpdateAddressRequest request, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var customerExists = await db.Database.SqlQueryRaw<Guid>(
            "SELECT id AS \"Value\" FROM store.user_accounts WHERE id = {0} FOR UPDATE", customerId)
            .FirstOrDefaultAsync(ct);

        if (customerExists == Guid.Empty)
        {
            throw new AccountFailure("NOT_FOUND", "Không tìm thấy tài khoản.");
        }

        var address = await db.CustomerAddresses
            .FirstOrDefaultAsync(a => a.Id == addressId && a.CustomerId == customerId, ct);

        if (address is null)
        {
            throw new AccountFailure("NOT_FOUND", "Không tìm thấy địa chỉ.");
        }

        if (request.IsDefault && !address.IsDefault)
        {
            await db.CustomerAddresses
                .Where(a => a.CustomerId == customerId && a.IsDefault && a.Id != addressId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.IsDefault, false)
                    .SetProperty(a => a.UpdatedAt, DateTime.UtcNow), ct);
            address.IsDefault = true;
        }
        else if (!request.IsDefault && address.IsDefault)
        {
            address.IsDefault = false;
        }

        address.RecipientName = request.RecipientName.Trim();
        address.Phone = request.Phone.Trim();
        address.AddressLine = request.AddressLine.Trim();
        address.Ward = string.IsNullOrWhiteSpace(request.Ward) ? null : request.Ward.Trim();
        address.Province = request.Province.Trim();
        address.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new AddressDto(
            address.Id,
            address.RecipientName,
            address.Phone,
            address.AddressLine,
            address.Ward,
            address.Province,
            address.IsDefault,
            address.CreatedAt);
    }

    public async Task DeleteAddressAsync(Guid customerId, Guid addressId, CancellationToken ct = default)
    {
        var address = await db.CustomerAddresses
            .FirstOrDefaultAsync(a => a.Id == addressId && a.CustomerId == customerId, ct);

        if (address is null)
        {
            throw new AccountFailure("NOT_FOUND", "Không tìm thấy địa chỉ.");
        }

        var inUse = await db.SalesOrders.AnyAsync(o => o.ShippingAddressId == addressId, ct);
        if (inUse)
        {
            throw new AccountFailure("ADDRESS_IN_USE", "Địa chỉ đã gắn với đơn hàng, không thể xóa.");
        }

        try
        {
            db.CustomerAddresses.Remove(address);
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            throw new AccountFailure("ADDRESS_IN_USE", "Địa chỉ đã gắn với đơn hàng, không thể xóa.");
        }
    }
}
