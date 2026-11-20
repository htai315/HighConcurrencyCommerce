using System.ComponentModel.DataAnnotations;
using System.Text;
using HighConcurrencyCommerce.Application.Auth;
using HighConcurrencyCommerce.Infrastructure.Auth;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace HighConcurrencyCommerce.Api.Auth;

public static class AuthMaintenance
{
    public static async Task RunAsync(IServiceProvider services, string command)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var env = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();

        if (command == "--migrate-auth")
        {
            if (env.IsProduction())
                throw new InvalidOperationException("Lệnh migrate-auth chỉ dùng môi trường Development/Testing. Production cần migration review riêng.");
            foreach (var schema in new[]
            {
                "Database.Migrations.002_auth_sessions.sql",
                "Database.Migrations.004_google_auth.sql",
                "Database.Migrations.005_auth_email_recovery.sql",
                "Database.Migrations.006_internal_staff_invites.sql"
            })
            {
                using var stream = typeof(AdminBootstrap).Assembly.GetManifestResourceStream(schema)
                    ?? throw new InvalidOperationException($"Missing {schema}");
                using var reader = new StreamReader(stream);
                await db.Database.ExecuteSqlRawAsync(await reader.ReadToEndAsync());
            }
            Console.WriteLine("Đã áp dụng schema auth local (002, 004, 005 & 006). Không tạo tài khoản.");
            return;
        }
        if (command == "--seed-demo")
        {
            var isDemoSeedEnabled = config.GetValue<bool>("DemoSeed:Enabled");
            var isNonProd = env.IsDevelopment() || env.EnvironmentName is "Testing" or "Local";
            if (!isDemoSeedEnabled || !isNonProd)
            {
                throw new InvalidOperationException(
                    "Lệnh seed-demo bị từ chối: Yêu cầu đồng thời DemoSeed:Enabled=true VÀ môi trường không phải Production (Development/Testing/Local). Tuyệt đối không chạy trên môi trường thực tế.");
            }
            using var stream = typeof(AdminBootstrap).Assembly.GetManifestResourceStream("DemoSeed.sql")
                ?? throw new InvalidOperationException("Missing DemoSeed.sql");
            using var reader = new StreamReader(stream);
            await db.Database.ExecuteSqlRawAsync(await reader.ReadToEndAsync());
            Console.WriteLine("Đã áp dụng dữ liệu demo (007_demo_seed.sql): 3 tài khoản demo (Admin/Staff/Customer: DemoPass123!@#), danh mục, sản phẩm, thông số so sánh, SKU và tồn kho.");
            return;
        }
        Console.Write("Email Admin: ");
        var email = Console.ReadLine() ?? "";
        Console.Write("Họ tên: ");
        var name = Console.ReadLine() ?? "";
        Console.Write("Mật khẩu (12–128 ký tự, có chữ hoa/thường, số, ký tự đặc biệt; không hiển thị): ");
        var password = ReadSecret();
        Console.Write("Nhập lại mật khẩu: ");
        if (ReadSecret() != password) throw new ValidationException("Hai mật khẩu không khớp.");
        var created = await scope.ServiceProvider.GetRequiredService<AdminBootstrap>().CreateAsync(
            new RegisterRequest { Email = email, FullName = name, Password = password, ConfirmPassword = password }, default);
        Console.WriteLine(created ? "Đã tạo Admin đầu tiên." : "Đã có Admin; không tạo thêm tài khoản.");
    }

    private static string ReadSecret()
    {
        // Allows CI to supply stdin without putting secrets in process arguments.
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return text.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; }
            else if (!char.IsControl(key.KeyChar) && text.Length < 129) text.Append(key.KeyChar);
        }
    }
}
