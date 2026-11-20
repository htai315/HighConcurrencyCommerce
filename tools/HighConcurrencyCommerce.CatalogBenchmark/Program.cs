using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HighConcurrencyCommerce.Infrastructure.Migrations;
using Microsoft.Extensions.Configuration;
using Npgsql;

const int sampleCount = 25;
const int commandTimeoutSeconds = 2;
var keepDatabase = args.Contains("--keep", StringComparer.OrdinalIgnoreCase);
var configuration = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .AddEnvironmentVariables()
    .Build();
var sourceConnection = Environment.GetEnvironmentVariable("HCC_PERF_POSTGRES")
    ?? configuration.GetConnectionString("StoreDb")
    ?? throw new InvalidOperationException("Set HCC_PERF_POSTGRES or the StoreDb user secret.");
var source = new NpgsqlConnectionStringBuilder(sourceConnection);
if (source.Host is not ("localhost" or "127.0.0.1" or "::1"))
    throw new InvalidOperationException("The benchmark only allows loopback PostgreSQL hosts.");

var dropOnly = args.SingleOrDefault(argument => argument.StartsWith("--drop=", StringComparison.OrdinalIgnoreCase))?[7..];
if (dropOnly is not null)
{
    if (!System.Text.RegularExpressions.Regex.IsMatch(dropOnly, "^hcc_perf_[0-9a-f]{32}$"))
        throw new InvalidOperationException("Refusing to drop a non-benchmark database.");
    var cleanupSettings = new NpgsqlConnectionStringBuilder(sourceConnection) { Database = "postgres", Pooling = false };
    await using var cleanupConnection = new NpgsqlConnection(cleanupSettings.ConnectionString);
    await cleanupConnection.OpenAsync();
    await using var cleanup = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{dropOnly}\" WITH (FORCE)", cleanupConnection);
    await cleanup.ExecuteNonQueryAsync();
    Console.WriteLine("Dropped retained disposable benchmark database.");
    return;
}

var databaseName = "hcc_perf_" + Guid.NewGuid().ToString("N");
var adminSettings = new NpgsqlConnectionStringBuilder(sourceConnection) { Database = "postgres", Pooling = false };
var benchmarkSettings = new NpgsqlConnectionStringBuilder(sourceConnection) { Database = databaseName, Pooling = false };
await using var admin = new NpgsqlConnection(adminSettings.ConnectionString);
await admin.OpenAsync();
await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin))
    await create.ExecuteNonQueryAsync();

try
{
    Console.WriteLine($"Created disposable benchmark database {databaseName}.");
    var migrationRunner = new DatabaseMigrationRunner(
        benchmarkSettings.ConnectionString,
        EmbeddedDatabaseMigrations.Load(),
        lockTimeout: TimeSpan.FromSeconds(30));
    await migrationRunner.RunAsync();

    await using var connection = new NpgsqlConnection(benchmarkSettings.ConnectionString);
    await connection.OpenAsync();
    var seedPath = Path.Combine(AppContext.BaseDirectory, "catalog_search_benchmark_seed.sql");
    await using (var seed = new NpgsqlCommand(await File.ReadAllTextAsync(seedPath), connection) { CommandTimeout = 300 })
        await seed.ExecuteNonQueryAsync();
    await using (var vacuum = new NpgsqlCommand("VACUUM (ANALYZE) store.products, store.product_skus, store.inventory_balances, store.sales_orders, store.sales_order_items, store.shipments, store.product_reviews", connection) { CommandTimeout = 300 })
        await vacuum.ExecuteNonQueryAsync();

    var counts = await ReadCountsAsync(connection);
    if (counts.Products < 10000 || counts.Skus < 30000 || counts.Brands < 20 || counts.Categories < 20)
        throw new InvalidOperationException($"Benchmark dataset is incomplete: {counts}.");

    var cases = BenchmarkCases();
    var results = new List<BenchmarkResult>(cases.Count);
    foreach (var benchmarkCase in cases)
    {
        var plan = await ExplainAsync(connection, benchmarkCase.Sql);
        if (benchmarkCase.RequiredIndexPrefix is { } indexPrefix
            && !plan.Indexes.Any(index => index.StartsWith(indexPrefix, StringComparison.Ordinal)))
            throw new InvalidOperationException($"{benchmarkCase.Name} did not use required index prefix {indexPrefix}. Plan indexes: {string.Join(", ", plan.Indexes)}. Plan: {plan.Json}");
        if (benchmarkCase.GuardSearchTablesAgainstSeqScan
            && plan.SequentialScans.Any(table => table is "products" or "product_skus"))
            throw new InvalidOperationException($"{benchmarkCase.Name} performed a sequential scan on {string.Join(", ", plan.SequentialScans)}.");

        await ExecuteAsync(connection, benchmarkCase.Sql); // warm-up
        var samples = new double[sampleCount];
        for (var index = 0; index < samples.Length; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            await ExecuteAsync(connection, benchmarkCase.Sql);
            samples[index] = stopwatch.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);
        var p95 = samples[(int)Math.Ceiling(samples.Length * 0.95) - 1];
        results.Add(new BenchmarkResult(benchmarkCase.Name, p95, plan.ExecutionMilliseconds, plan.Indexes, plan.SequentialScans));
        Console.WriteLine($"{benchmarkCase.Name}: p95 {p95:F2} ms; plan {plan.ExecutionMilliseconds:F2} ms; indexes [{string.Join(", ", plan.Indexes)}]");
    }

    var reportPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "performance", "catalog-search-benchmark-latest.md"));
    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
    await File.WriteAllTextAsync(reportPath, BuildReport(counts, results));
    Console.WriteLine($"Report written to {reportPath}");
}
finally
{
    NpgsqlConnection.ClearAllPools();
    if (!keepDatabase)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(databaseName, "^hcc_perf_[0-9a-f]{32}$"))
            throw new InvalidOperationException("Refusing to drop a non-benchmark database.");
        await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
        Console.WriteLine("Dropped disposable benchmark database.");
    }
    else Console.WriteLine($"Kept benchmark database {databaseName} by request.");
}

static async Task<(long Products, long Skus, long Brands, long Categories)> ReadCountsAsync(NpgsqlConnection connection)
{
    const string sql = "SELECT (SELECT count(*) FROM store.products), (SELECT count(*) FROM store.product_skus), (SELECT count(*) FROM store.brands), (SELECT count(*) FROM store.categories)";
    await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = commandTimeoutSeconds };
    await using var reader = await command.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
}

static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
{
    await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = commandTimeoutSeconds };
    await command.ExecuteScalarAsync();
}

static async Task<PlanSummary> ExplainAsync(NpgsqlConnection connection, string sql)
{
    await using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + sql, connection) { CommandTimeout = commandTimeoutSeconds };
    var raw = (string?)await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("EXPLAIN returned no JSON.");
    using var document = JsonDocument.Parse(raw);
    var root = document.RootElement[0].GetProperty("Plan");
    var indexes = new HashSet<string>(StringComparer.Ordinal);
    var sequentialScans = new HashSet<string>(StringComparer.Ordinal);
    Visit(root, indexes, sequentialScans);
    return new PlanSummary(root.GetProperty("Actual Total Time").GetDouble(), indexes.Order().ToArray(), sequentialScans.Order().ToArray(), raw);
}

static void Visit(JsonElement node, HashSet<string> indexes, HashSet<string> sequentialScans)
{
    if (node.TryGetProperty("Index Name", out var indexName)) indexes.Add(indexName.GetString()!);
    if (node.TryGetProperty("Node Type", out var nodeType)
        && nodeType.GetString() == "Seq Scan"
        && node.TryGetProperty("Relation Name", out var relation))
        sequentialScans.Add(relation.GetString()!);
    if (node.TryGetProperty("Plans", out var children))
        foreach (var child in children.EnumerateArray()) Visit(child, indexes, sequentialScans);
}

static List<BenchmarkCase> BenchmarkCases() =>
[
    new("Exact SKU", """
        SELECT p.id FROM store.product_skus s
        JOIN store.products p ON p.id = s.product_id
        WHERE s.sku_status = 'ACTIVE' AND p.publication_status = 'PUBLISHED'
          AND s.search_sku_norm = 'bench-09999-2' LIMIT 8
        """, "ix_product_skus_search_sku_trgm", true),
    new("Accent-insensitive title prefix", """
        SELECT p.id FROM store.products p
        WHERE p.publication_status = 'PUBLISHED'
          AND p.search_title_norm ILIKE 'vot fa246d02 1b4ba419 4e33b4aa 8dedc7fb benchmark 09999%'
        ORDER BY p.created_at DESC, p.id LIMIT 12
        """, "ix_products_search_title_trgm", true),
    new("Fuzzy title", """
        SELECT p.id FROM store.products p
        WHERE p.publication_status = 'PUBLISHED'
          AND p.search_title_norm % 'vot fa246d02 1b4ba41x 4e33b4aa 8dedc7fb benchmark 09999'
        ORDER BY similarity(p.search_title_norm, 'vot fa246d02 1b4ba41x 4e33b4aa 8dedc7fb benchmark 09999') DESC, p.id LIMIT 12
        """, "ix_products_search_title_trgm", true),
    new("Combined search facets", """
        SELECT p.id FROM store.products p
        WHERE p.publication_status = 'PUBLISHED'
          AND p.search_title_norm ILIKE '%fa246d02%'
          AND p.brand_id = md5('hcc-benchmark-brand-19')::uuid
          AND EXISTS (SELECT 1 FROM store.product_skus s JOIN store.inventory_balances b ON b.product_sku_id = s.id
                      WHERE s.product_id = p.id AND s.sku_status = 'ACTIVE'
                        AND s.regular_price BETWEEN 500000 AND 4000000 AND b.available_qty > 0)
          AND (SELECT avg(r.rating) FROM store.product_reviews r
               WHERE r.product_id = p.id AND r.review_status = 'PUBLISHED' AND r.deleted_at IS NULL) >= 1
        LIMIT 12
        """, "ix_products_search_title_trgm", true),
    new("Suggestion", """
        SELECT p.id, p.title FROM store.products p
        WHERE p.publication_status = 'PUBLISHED'
          AND (p.search_title_norm ILIKE '%fa246d02%'
               OR p.search_title_norm % 'vot fa246d02 1b4ba419 4e33b4aa 8dedc7fb benchmark 09999')
        ORDER BY similarity(p.search_title_norm, 'vot fa246d02 1b4ba419 4e33b4aa 8dedc7fb benchmark 09999') DESC, p.id LIMIT 8
        """, "ix_products_search_title_trgm", true),
    new("Best-selling 90 days", """
        SELECT p.id, coalesce(sum(oi.quantity), 0) AS sold
        FROM store.products p
        LEFT JOIN store.product_skus s ON s.product_id = p.id AND s.sku_status = 'ACTIVE'
        LEFT JOIN store.sales_order_items oi ON oi.product_sku_id = s.id
        LEFT JOIN store.sales_orders o ON o.id = oi.order_id AND o.order_status = 'COMPLETED' AND o.updated_at >= now() - interval '90 days'
        LEFT JOIN store.shipments sh ON sh.order_id = o.id AND sh.shipment_status = 'DELIVERED'
        WHERE p.publication_status = 'PUBLISHED'
        GROUP BY p.id ORDER BY sold DESC, p.id LIMIT 12
        """, null, false),
    new("Deep page", """
        SELECT p.id FROM store.products p
        WHERE p.publication_status = 'PUBLISHED'
        ORDER BY p.created_at DESC, p.id OFFSET 9000 LIMIT 12
        """, null, false),
];

static string BuildReport((long Products, long Skus, long Brands, long Categories) counts, IReadOnlyList<BenchmarkResult> results)
{
    var report = new StringBuilder();
    report.AppendLine("# T50.8 Catalog Search Benchmark");
    report.AppendLine();
    report.AppendLine($"Generated: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss 'UTC'}");
    report.AppendLine($"Dataset: {counts.Products:N0} products, {counts.Skus:N0} SKUs, {counts.Brands:N0} brands, {counts.Categories:N0} categories.");
    report.AppendLine($"Samples: {sampleCount} after one warm-up; command/statement timeout: {commandTimeoutSeconds} seconds.");
    report.AppendLine();
    report.AppendLine("| Case | p95 client ms | EXPLAIN execution ms | Reference target | Indexes | Seq scans |");
    report.AppendLine("|---|---:|---:|---|---|---|");
    foreach (var result in results)
    {
        var target = result.Name == "Suggestion" ? "< 150 ms" : "< 300 ms";
        report.AppendLine($"| {result.Name} | {result.P95Milliseconds:F2} | {result.PlanMilliseconds:F2} | {target} | {string.Join(", ", result.Indexes)} | {string.Join(", ", result.SequentialScans)} |");
    }
    report.AppendLine();
    report.AppendLine("Timing targets are informative and are not hard assertions in the normal integration suite. The hard gate requires the expected trigram indexes and rejects sequential scans of `products`/`product_skus` for the five search/suggestion cases.");
    report.AppendLine("The dataset is created in a disposable loopback-only `hcc_perf_<guid>` database and is never part of demo or production migrations.");
    return report.ToString();
}

sealed record BenchmarkCase(string Name, string Sql, string? RequiredIndexPrefix, bool GuardSearchTablesAgainstSeqScan);
sealed record PlanSummary(double ExecutionMilliseconds, IReadOnlyList<string> Indexes, IReadOnlyList<string> SequentialScans, string Json);
sealed record BenchmarkResult(string Name, double P95Milliseconds, double PlanMilliseconds, IReadOnlyList<string> Indexes, IReadOnlyList<string> SequentialScans);
