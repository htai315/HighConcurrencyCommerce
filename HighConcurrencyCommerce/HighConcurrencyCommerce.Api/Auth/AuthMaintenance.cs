using System.ComponentModel.DataAnnotations;
using System.Text;
using HighConcurrencyCommerce.Application.Auth;
using HighConcurrencyCommerce.Infrastructure.Auth;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HighConcurrencyCommerce.Api.Auth;

public static class AuthMaintenance
{
    public static async Task RunAsync(IServiceProvider services, string command)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        if (command == "--migrate-auth")
        {
            var connection = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());
            if (connection.Host is not ("localhost" or "127.0.0.1" or "::1"))
                throw new InvalidOperationException("Lệnh migrate-auth chỉ dùng PostgreSQL local. Production cần migration review riêng.");
            using var stream = typeof(AdminBootstrap).Assembly.GetManifestResourceStream("AuthSchema.sql")
                ?? throw new InvalidOperationException("Missing AuthSchema.sql");
            using var reader = new StreamReader(stream);
            await db.Database.ExecuteSqlRawAsync(await reader.ReadToEndAsync());
            Console.WriteLine("Đã áp dụng schema auth local. Không tạo tài khoản.");
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
