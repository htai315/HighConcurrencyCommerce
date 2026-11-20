using HighConcurrencyCommerce.Api.Auth;
using HighConcurrencyCommerce.Application.Wishlist;
using Microsoft.AspNetCore.Diagnostics;

namespace HighConcurrencyCommerce.Api.Wishlist;

public sealed class WishlistExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not WishlistFailure failure) return false;
        var (status, title) = failure.Code switch
        {
            "VALIDATION_ERROR" => (400, failure.Message.Length > 0 ? failure.Message : "Dữ liệu yêu cầu không hợp lệ."),
            "UNAUTHORIZED" => (401, failure.Message.Length > 0 ? failure.Message : "Yêu cầu đăng nhập tài khoản khách hàng."),
            "FORBIDDEN" => (403, failure.Message.Length > 0 ? failure.Message : "Không có quyền thực hiện thao tác này."),
            "PRODUCT_NOT_FOUND" => (404, failure.Message.Length > 0 ? failure.Message : "Không tìm thấy sản phẩm."),
            "WISHLIST_ITEM_NOT_FOUND" => (404, failure.Message.Length > 0 ? failure.Message : "Không tìm thấy sản phẩm trong danh sách yêu thích."),
            "WISHLIST_LIMIT_REACHED" => (409, failure.Message.Length > 0 ? failure.Message : "Danh sách yêu thích đã đạt giới hạn tối đa 500 sản phẩm."),
            "INVALID_ALERT_SETTINGS" => (400, failure.Message.Length > 0 ? failure.Message : "Cấu hình cảnh báo không hợp lệ."),
            "EMAIL_CHANNEL_REQUIRES_VERIFIED_EMAIL" => (400, failure.Message.Length > 0 ? failure.Message : "Email cần được xác minh."),
            "WISHLIST_ALERT_PRODUCT_UNAVAILABLE" => (409, failure.Message.Length > 0 ? failure.Message : "Sản phẩm không còn mở bán."),
            "STALE_WISHLIST_ALERT_SETTINGS" => (409, failure.Message.Length > 0 ? failure.Message : "Cấu hình cảnh báo đã thay đổi."),
            "WISHLIST_ALERT_REVISION_EXHAUSTED" => (409, failure.Message.Length > 0 ? failure.Message : "Cấu hình cảnh báo không thể cập nhật thêm."),
            _ => (failure.Status != 0 ? failure.Status : 400, failure.Message.Length > 0 ? failure.Message : "Yêu cầu không thể thực hiện.")
        };
        if (failure.CurrentRevision.HasValue)
        {
            await Results.Problem(
                statusCode: status,
                title: title,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = failure.Code,
                    ["correlationId"] = context.TraceIdentifier,
                    ["currentRevision"] = failure.CurrentRevision.Value
                }).ExecuteAsync(context);
        }
        else
        {
            await AuthProblems.Write(context, status, failure.Code, title);
        }
        return true;
    }
}

