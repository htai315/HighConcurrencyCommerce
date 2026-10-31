using HighConcurrencyCommerce.Api.Auth;
using HighConcurrencyCommerce.Application.Cart;
using Microsoft.AspNetCore.Diagnostics;

namespace HighConcurrencyCommerce.Api.Cart;

public sealed class CartExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not CartFailure failure) return false;
        var status = failure.Code switch
        {
            "NOT_FOUND" or "SKU_NOT_AVAILABLE" => 404,
            "CART_CLOSED" or "FLASH_SALE_NOT_AVAILABLE" => 409,
            _ => 400
        };
        await AuthProblems.Write(context, status, failure.Code,
            string.IsNullOrWhiteSpace(failure.Message) ? "Yêu cầu giỏ hàng không hợp lệ." : failure.Message);
        return true;
    }
}
