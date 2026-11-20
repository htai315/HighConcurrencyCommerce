using HighConcurrencyCommerce.Api.Auth;
using HighConcurrencyCommerce.Application.Vouchers;
using Microsoft.AspNetCore.Diagnostics;

namespace HighConcurrencyCommerce.Api.Vouchers;

public sealed class VoucherExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not VoucherFailure failure) return false;

        var status = failure.Status != 0 ? failure.Status : failure.Code switch
        {
            "NOT_FOUND" or "VOUCHER_NOT_FOUND" => 404,
            "CONFLICT" or "INVALID_STATE" or "VOUCHER_NOT_ACTIVE" or "VOUCHER_NOT_STARTED"
                or "VOUCHER_EXPIRED" or "VOUCHER_MINIMUM_NOT_MET" or "VOUCHER_USAGE_LIMIT_REACHED"
                or "VOUCHER_CUSTOMER_LIMIT_REACHED" or "VOUCHER_BUDGET_EXHAUSTED" or "VOUCHER_DISCOUNT_INVALID" => 409,
            "UNAUTHORIZED" => 401,
            "FORBIDDEN" => 403,
            "VALIDATION_ERROR" => 400,
            _ => 400
        };

        var title = failure.Message.Length > 0 ? failure.Message : "Yêu cầu không thể thực hiện.";
        await AuthProblems.Write(context, status, failure.Code, title);
        return true;
    }
}

