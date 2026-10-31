using HighConcurrencyCommerce.Application.Auth;
using Microsoft.AspNetCore.Diagnostics;

namespace HighConcurrencyCommerce.Api.Auth;

public static class AuthProblems
{
    public static Task Write(HttpContext context, int status, string code, string title) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?>
        { ["code"] = code, ["correlationId"] = context.TraceIdentifier }).ExecuteAsync(context);
}
public sealed class AuthExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not AuthFailure failure) return false;
        var (status, title) = failure.Code switch
        {
            "INVALID_CREDENTIALS" => (401, "Email hoặc mật khẩu không đúng."),
            "UNAUTHORIZED" => (401, "Vui lòng đăng nhập."),
            "FORBIDDEN" => (403, "Bạn không có quyền truy cập."),
            "NOT_FOUND" => (404, "Không tìm thấy tài khoản."),
            "SELF_STATUS_CHANGE" => (409, "Không thể đổi trạng thái tài khoản của chính mình."),
            _ => (400, "Mật khẩu hiện tại không đúng hoặc mật khẩu mới không hợp lệ.")
        };
        await AuthProblems.Write(context, status, failure.Code, title);
        return true;
    }
}

