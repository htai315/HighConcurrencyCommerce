using HighConcurrencyCommerce.Api.Auth;
using HighConcurrencyCommerce.Application.FlashSale;
using Microsoft.AspNetCore.Diagnostics;

namespace HighConcurrencyCommerce.Api.FlashSale;

public sealed class FlashSaleExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not FlashSaleFailure failure) return false;

        var (status, title) = failure.Code switch
        {
            "NOT_FOUND" => (404, failure.Message.Length > 0 ? failure.Message : "Không tìm thấy dữ liệu."),
            "CONFLICT" => (409, failure.Message.Length > 0 ? failure.Message : "Dữ liệu bị xung đột hoặc trùng lặp."),
            "INVALID_STATE_TRANSITION" => (409, failure.Message.Length > 0 ? failure.Message : "Trạng thái chiến dịch không cho phép thực hiện thao tác này."),
            "CANNOT_PUBLISH" => (400, failure.Message.Length > 0 ? failure.Message : "Không thể công bố chiến dịch Flash Sale."),
            "INSUFFICIENT_STOCK" => (400, failure.Message.Length > 0 ? failure.Message : "Tồn kho không đủ để đáp ứng quota Flash Sale."),
            "VALIDATION_ERROR" => (400, failure.Message.Length > 0 ? failure.Message : "Dữ liệu không hợp lệ."),
            _ => (400, failure.Message.Length > 0 ? failure.Message : "Yêu cầu không hợp lệ.")
        };

        await AuthProblems.Write(context, status, failure.Code, title);
        return true;
    }
}

