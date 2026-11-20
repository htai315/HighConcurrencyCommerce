using HighConcurrencyCommerce.Api.Auth;
using HighConcurrencyCommerce.Application.Inventory;
using Microsoft.AspNetCore.Diagnostics;

namespace HighConcurrencyCommerce.Api.Inventory;

public sealed class InventoryExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not InventoryFailure failure) return false;
        var (status, title) = failure.Code switch
        {
            "NOT_FOUND" => (404, failure.Message.Length > 0 ? failure.Message : "Không tìm thấy dữ liệu tồn kho."),
            "CONFLICT" => (409, failure.Message.Length > 0 ? failure.Message : "Giao dịch kho bị xung đột."),
            "IDEMPOTENCY_CONFLICT" => (409, failure.Message.Length > 0 ? failure.Message : "Yêu cầu có cùng sourceId nhưng nội dung payload khác với giao dịch đã ghi nhận."),
            "INVENTORY_LIMIT_EXCEEDED" => (409, failure.Message.Length > 0 ? failure.Message : "Số lượng tồn kho vượt quá giới hạn lưu trữ của hệ thống."),
            "ALREADY_DECIDED" => (409, failure.Message.Length > 0 ? failure.Message : "Yêu cầu điều chỉnh đã được xử lý."),
            "INSUFFICIENT_AVAILABLE" => (409, failure.Message.Length > 0 ? failure.Message : "Tồn khả dụng không đủ để thực hiện điều chỉnh."),
            "SELF_REVIEW" => (409, failure.Message.Length > 0 ? failure.Message : "Người tạo yêu cầu không được tự duyệt."),
            "INVALID_ARGUMENT" => (400, failure.Message.Length > 0 ? failure.Message : "Thông tin nhập kho không hợp lệ."),
            _ => (400, failure.Message.Length > 0 ? failure.Message : "Yêu cầu không hợp lệ.")
        };
        await AuthProblems.Write(context, status, failure.Code, title);
        return true;
    }
}

