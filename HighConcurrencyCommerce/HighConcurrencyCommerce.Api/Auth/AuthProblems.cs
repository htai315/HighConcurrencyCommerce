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
            "GOOGLE_AUTH_INVALID" => (400, "Thông tin xác thực Google không hợp lệ hoặc đã hết hạn."),
            "GOOGLE_AUTH_UNAVAILABLE" => (503, "Không thể kết nối đến máy chủ Google. Vui lòng thử lại sau."),
            "INVALID_CHALLENGE" => (400, "Phiên bảo vệ xác thực không hợp lệ hoặc đã hết hạn. Vui lòng thử lại."),
            "ACCOUNT_LINK_REQUIRED" => (409, "Email này đã được đăng ký bằng mật khẩu. Vui lòng đăng nhập bằng mật khẩu rồi liên kết Google trong mục Bảo mật."),
            "IDENTITY_ALREADY_LINKED" => (409, "Tài khoản Google này đã được liên kết với một tài khoản khác."),
            "LAST_AUTH_METHOD" => (400, "Không thể hủy liên kết Google khi tài khoản chưa cài đặt mật khẩu đăng nhập."),
            "EMAIL_MISMATCH" => (400, "Email của tài khoản Google phải trùng khớp với email của tài khoản đang đăng nhập."),
            "INVALID_PASSWORD" => (400, "Mật khẩu hiện tại không đúng hoặc mật khẩu mới trùng mật khẩu cũ."),
            "SESSION_CONFLICT" => (409, "Bạn đang đăng nhập bằng một tài khoản khác. Vui lòng đăng xuất trước khi đăng nhập tài khoản này."),
            "EMAIL_NOT_VERIFIED" => (403, "Email chưa được xác minh. Vui lòng kiểm tra hộp thư hoặc yêu cầu gửi lại email xác minh."),
            _ => (400, "Yêu cầu không hợp lệ.")
        };
        await AuthProblems.Write(context, status, failure.Code, title);
        return true;
    }
}

public sealed class AuthRecoveryExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not AuthRecoveryFailure failure) return false;
        var (status, title) = failure.Code switch
        {
            "INVALID_OR_EXPIRED_TOKEN" => (400, failure.Message),
            "PASSWORD_RESET_SAME_AS_CURRENT" => (400, failure.Message),
            "EMAIL_NOT_VERIFIED" => (403, failure.Message),
            "RATE_LIMITED" => (429, failure.Message),
            _ => (failure.Status, failure.Message)
        };
        await AuthProblems.Write(context, status, failure.Code, title);
        return true;
    }
}

public sealed class AdminAccountExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not HighConcurrencyCommerce.Application.AdminAccounts.AdminAccountFailure failure) return false;
        var (status, title) = failure.Code switch
        {
            "FORBIDDEN" => (403, "Bạn không có quyền thực hiện thao tác này."),
            "NOT_FOUND" => (404, "Không tìm thấy tài khoản."),
            "SELF_STATUS_CHANGE" => (409, "Không thể thay đổi trạng thái tài khoản của chính mình."),
            "SELF_REVOKE_PROHIBITED" => (400, "Không thể thu hồi phiên đăng nhập của chính mình qua endpoint này."),
            "EMAIL_ALREADY_EXISTS" => (409, "Email này đã tồn tại trong hệ thống."),
            "ACCOUNT_LOCKED" => (403, failure.Message),
            "RATE_LIMITED" => (429, failure.Message),
            _ => (failure.Status, failure.Message)
        };
        await AuthProblems.Write(context, status, failure.Code, title);
        return true;
    }
}
