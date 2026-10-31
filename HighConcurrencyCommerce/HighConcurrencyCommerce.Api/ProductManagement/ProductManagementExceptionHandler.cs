using HighConcurrencyCommerce.Api.Auth;
using HighConcurrencyCommerce.Application.ProductManagement;
using Microsoft.AspNetCore.Diagnostics;

namespace HighConcurrencyCommerce.Api.ProductManagement;

public sealed class ProductManagementExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not ProductManagementFailure failure) return false;
        var (status, title) = failure.Code switch
        {
            "NOT_FOUND" => (404, failure.Message.Length > 0 ? failure.Message : "Không tìm thấy dữ liệu."),
            "CONFLICT" => (409, failure.Message.Length > 0 ? failure.Message : "Dữ liệu bị trùng lặp."),
            "INVALID_STATE" => (400, failure.Message.Length > 0 ? failure.Message : "Trạng thái sản phẩm không hợp lệ."),
            "CANNOT_PUBLISH" => (400, failure.Message.Length > 0 ? failure.Message : "Không thể công bố sản phẩm."),
            _ => (400, failure.Message.Length > 0 ? failure.Message : "Yêu cầu không hợp lệ.")
        };
        await AuthProblems.Write(context, status, failure.Code, title);
        return true;
    }
}

