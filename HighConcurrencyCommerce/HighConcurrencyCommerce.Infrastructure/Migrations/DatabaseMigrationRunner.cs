using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace HighConcurrencyCommerce.Infrastructure.Migrations;

public sealed class DatabaseMigrationRunner
{
    // Stable, application-specific session advisory lock key ("HCCMIGR").
    private const long AdvisoryLockKey = 0x4843434D494752;
    private const string HistoryTable = "public.schema_migrations";
    private readonly string connectionString;
    private readonly IReadOnlyList<DatabaseMigration> migrations;
    private readonly ILogger<DatabaseMigrationRunner> logger;
    private readonly TimeSpan lockTimeout;

    public DatabaseMigrationRunner(
        string connectionString,
        IReadOnlyList<DatabaseMigration> migrations,
        ILogger<DatabaseMigrationRunner>? logger = null,
        TimeSpan? lockTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        this.connectionString = connectionString;
        this.migrations = ValidateManifest(migrations);
        this.logger = logger ?? NullLogger<DatabaseMigrationRunner>.Instance;
        this.lockTimeout = lockTimeout ?? TimeSpan.FromMinutes(2);
    }

    public async Task<DatabaseMigrationResult> RunAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await AcquireLockAsync(connection, ct);
        try
        {
            await EnsureHistoryTableAsync(connection, ct);
            var applied = await ReadAppliedAsync(connection, ct);
            RejectUnknownVersions(applied);

            var appliedCount = 0;
            var baselineCount = 0;
            var alreadyAppliedCount = 0;
            foreach (var migration in migrations)
            {
                if (applied.TryGetValue(migration.Version, out var existing))
                {
                    EnsureChecksumMatches(migration, existing.Checksum);
                    alreadyAppliedCount++;
                    continue;
                }

                var baseline = migration.Version == "001" && await CoreSchemaExistsAsync(connection, ct);
                var stopwatch = Stopwatch.StartNew();
                if (!baseline)
                    await ExecuteMigrationAsync(connection, migration, ct);
                stopwatch.Stop();

                await RecordAppliedAsync(connection, migration, baseline, stopwatch.ElapsedMilliseconds, ct);
                applied[migration.Version] = new AppliedMigration(migration.Checksum);
                if (baseline) baselineCount++; else appliedCount++;
                logger.LogInformation(
                    "Database migration {Version} {Mode} in {ElapsedMs} ms.",
                    migration.Version,
                    baseline ? "baselined" : "applied",
                    stopwatch.ElapsedMilliseconds);
            }

            return new DatabaseMigrationResult(appliedCount, baselineCount, alreadyAppliedCount);
        }
        finally
        {
            await ReleaseLockAsync(connection);
        }
    }

    public async Task ValidateAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await AcquireLockAsync(connection, ct);
        try
        {
            if (!await HistoryTableExistsAsync(connection, ct))
                throw new DatabaseMigrationException(
                    "Database chưa được quản lý bởi migrator. Hãy chạy '--migrate-database' trước khi khởi động API.");

            var applied = await ReadAppliedAsync(connection, ct);
            RejectUnknownVersions(applied);
            foreach (var migration in migrations)
            {
                if (!applied.TryGetValue(migration.Version, out var existing))
                    throw new DatabaseMigrationException(
                        $"Database còn thiếu migration {migration.Version}. Hãy chạy '--migrate-database' trước khi khởi động API.");
                EnsureChecksumMatches(migration, existing.Checksum);
            }
        }
        finally
        {
            await ReleaseLockAsync(connection);
        }
    }

    private static IReadOnlyList<DatabaseMigration> ValidateManifest(IReadOnlyList<DatabaseMigration> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count == 0) throw new DatabaseMigrationException("Migration manifest không được rỗng.");
        var duplicate = source.GroupBy(m => m.Version, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new DatabaseMigrationException($"Migration version bị trùng: {duplicate.Key}.");
        if (source.Any(m => !System.Text.RegularExpressions.Regex.IsMatch(m.Version, "^[0-9]{3}$")))
            throw new DatabaseMigrationException("Migration version phải có đúng 3 chữ số.");
        var ordered = source.OrderBy(m => m.Version, StringComparer.Ordinal).ToArray();
        if (!ordered.Select(m => m.Version).SequenceEqual(source.Select(m => m.Version), StringComparer.Ordinal))
            throw new DatabaseMigrationException("Migration manifest phải được sắp xếp tăng dần theo version.");
        return ordered;
    }

    private async Task AcquireLockAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        while (true)
        {
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
            command.Parameters.AddWithValue("key", AdvisoryLockKey);
            if (await command.ExecuteScalarAsync(ct) is true) return;
            if (started.Elapsed >= lockTimeout)
                throw new DatabaseMigrationException(
                    $"Không lấy được database migration lock sau {lockTimeout.TotalSeconds:0} giây.");
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
    }

    private static async Task ReleaseLockAsync(NpgsqlConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open) return;
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
            command.Parameters.AddWithValue("key", AdvisoryLockKey);
            await command.ExecuteScalarAsync();
        }
        catch (Exception)
        {
            // Closing the session releases the advisory lock even when the explicit unlock cannot run.
        }
    }

    private static async Task EnsureHistoryTableAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS public.schema_migrations (
                version varchar(3) PRIMARY KEY,
                description varchar(200) NOT NULL,
                checksum char(64) NOT NULL CHECK (checksum ~ '^[0-9A-F]{64}$'),
                applied_at timestamptz NOT NULL DEFAULT now(),
                execution_ms bigint NOT NULL CHECK (execution_ms >= 0),
                application_version varchar(64) NOT NULL,
                execution_mode varchar(16) NOT NULL CHECK (execution_mode IN ('APPLIED', 'BASELINE'))
            )
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> HistoryTableExistsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT to_regclass('public.schema_migrations') IS NOT NULL", connection);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    private static async Task<bool> CoreSchemaExistsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT to_regclass('store.roles') IS NOT NULL", connection);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    private static async Task<Dictionary<string, AppliedMigration>> ReadAppliedAsync(
        NpgsqlConnection connection,
        CancellationToken ct)
    {
        var result = new Dictionary<string, AppliedMigration>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand($"SELECT version, checksum FROM {HistoryTable} ORDER BY version", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0), new AppliedMigration(reader.GetString(1)));
        return result;
    }

    private void RejectUnknownVersions(IReadOnlyDictionary<string, AppliedMigration> applied)
    {
        var expected = migrations.Select(m => m.Version).ToHashSet(StringComparer.Ordinal);
        var unknown = applied.Keys.Where(version => !expected.Contains(version)).Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
            throw new DatabaseMigrationException(
                $"Database có migration mới hơn/không được nhận diện: {string.Join(", ", unknown)}. Từ chối khởi động để tránh downgrade.");
    }

    private static void EnsureChecksumMatches(DatabaseMigration migration, string storedChecksum)
    {
        if (!string.Equals(migration.Checksum, storedChecksum, StringComparison.Ordinal))
            throw new DatabaseMigrationException(
                $"Checksum migration {migration.Version} không khớp. Không được sửa file migration đã áp dụng; hãy tạo migration mới.");
    }

    private static async Task ExecuteMigrationAsync(
        NpgsqlConnection connection,
        DatabaseMigration migration,
        CancellationToken ct)
    {
        try
        {
            await using var command = new NpgsqlCommand(migration.Sql, connection) { CommandTimeout = 300 };
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            try
            {
                await using var rollback = new NpgsqlCommand("ROLLBACK", connection);
                await rollback.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (Exception)
            {
                // Preserve the original migration error; connection disposal cleans up the session.
            }
            throw new DatabaseMigrationException($"Migration {migration.Version} thất bại.", ex);
        }
    }

    private static async Task RecordAppliedAsync(
        NpgsqlConnection connection,
        DatabaseMigration migration,
        bool baseline,
        long executionMs,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO public.schema_migrations
                (version, description, checksum, execution_ms, application_version, execution_mode)
            VALUES
                (@version, @description, @checksum, @executionMs, @applicationVersion, @executionMode)
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("version", migration.Version);
        command.Parameters.AddWithValue("description", migration.Description);
        command.Parameters.AddWithValue("checksum", migration.Checksum);
        command.Parameters.AddWithValue("executionMs", executionMs);
        command.Parameters.AddWithValue(
            "applicationVersion",
            typeof(DatabaseMigrationRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown");
        command.Parameters.AddWithValue("executionMode", baseline ? "BASELINE" : "APPLIED");
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed record AppliedMigration(string Checksum);
}

