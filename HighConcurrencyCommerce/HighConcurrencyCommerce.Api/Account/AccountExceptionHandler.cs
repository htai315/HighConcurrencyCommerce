using HighConcurrencyCommerce.Api.Auth;
using HighConcurrencyCommerce.Application.Account;
using Microsoft.AspNetCore.Diagnostics;

namespace HighConcurrencyCommerce.Api.Account;

public sealed class AccountExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not AccountFailure failure) return false;
        var (status, title) = failure.Code switch
        {
            "NOT_FOUND" => (404, failure.Message.Length > 0 ? failure.Message : "Không tìm thấy dữ liệu."),
            "ADDRESS_IN_USE" => (409, failure.Message.Length > 0 ? failure.Message : "Địa chỉ đã gắn với đơn hàng, không thể xóa."),
            "CONFLICT" => (409, failure.Message.Length > 0 ? failure.Message : "Xung đột dữ liệu."),
            _ => (400, failure.Message.Length > 0 ? failure.Message : "Dữ liệu không hợp lệ.")
        };
        await AuthProblems.Write(context, status, failure.Code, title);
        return true;
    }
}

