using System.Security.Cryptography;
using System.Text;

namespace HighConcurrencyCommerce.Infrastructure.Migrations;

public sealed record DatabaseMigration(
    string Version,
    string Description,
    string Sql,
    string Checksum)
{
    public static DatabaseMigration Create(string version, string description, string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
        return new DatabaseMigration(version, description, sql, checksum);
    }
}

public sealed record DatabaseMigrationResult(int AppliedCount, int BaselineCount, int AlreadyAppliedCount);

public sealed class DatabaseMigrationException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

