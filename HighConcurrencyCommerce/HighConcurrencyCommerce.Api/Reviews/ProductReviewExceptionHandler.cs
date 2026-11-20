using HighConcurrencyCommerce.Api.Auth;
using HighConcurrencyCommerce.Application.Reviews;
using Microsoft.AspNetCore.Diagnostics;

namespace HighConcurrencyCommerce.Api.Reviews;

public sealed class ProductReviewExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not ProductReviewFailure failure) return false;
        var (status, title) = failure.Code switch
        {
            "VALIDATION_ERROR" => (400, failure.Message.Length > 0 ? failure.Message : "Dữ liệu đánh giá không hợp lệ."),
            "PRODUCT_NOT_FOUND" => (404, failure.Message.Length > 0 ? failure.Message : "Không tìm thấy sản phẩm."),
            "REVIEW_NOT_FOUND" => (404, failure.Message.Length > 0 ? failure.Message : "Không tìm thấy đánh giá."),
            "REVIEW_NOT_ELIGIBLE" => (409, failure.Message.Length > 0 ? failure.Message : "Chỉ khách hàng đã nhận hàng thành công mới được đánh giá sản phẩm."),
            "REVIEW_ALREADY_EXISTS" => (409, failure.Message.Length > 0 ? failure.Message : "Bạn đã có đánh giá đang hoạt động cho sản phẩm này."),
            "INVALID_REVIEW_STATE" => (409, failure.Message.Length > 0 ? failure.Message : "Trạng thái đánh giá không hợp lệ cho thao tác này."),
            "CONCURRENT_CONFLICT" => (409, failure.Message.Length > 0 ? failure.Message : "Xung đột đồng thời. Vui lòng thử lại."),
            _ => (failure.Status != 0 ? failure.Status : 400, failure.Message.Length > 0 ? failure.Message : "Yêu cầu không thể thực hiện.")
        };
        await AuthProblems.Write(context, status, failure.Code, title);
        return true;
    }
}

