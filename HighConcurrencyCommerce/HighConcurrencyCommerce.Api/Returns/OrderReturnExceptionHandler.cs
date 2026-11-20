using HighConcurrencyCommerce.Api.Auth;
using HighConcurrencyCommerce.Application.Returns;
using Microsoft.AspNetCore.Diagnostics;

namespace HighConcurrencyCommerce.Api.Returns;

public sealed class OrderReturnExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not OrderReturnFailure failure) return false;

        var status = failure.Status != 0 ? failure.Status : failure.Code switch
        {
            "NOT_FOUND" or "ORDER_NOT_FOUND" or "RETURN_NOT_FOUND" or "PRODUCT_NOT_FOUND" or "PAYMENT_NOT_FOUND" => 404,
            "CONFLICT" or "RETURN_REQUEST_EXISTS" or "REFUND_REFERENCE_REUSED" or "ALREADY_RECEIVED" => 409,
            "UNAUTHORIZED" => 401,
            "FORBIDDEN" => 403,
            "VALIDATION_ERROR" or "INVALID_ARGUMENT" or "EMPTY_SELECTIONS"
                or "DUPLICATE_ITEM_SELECTION" or "INVALID_REQUESTED_QUANTITY"
                or "INVALID_REASON_CODE" or "NOTE_REQUIRED_FOR_OTHER"
                or "RETURN_NOT_ELIGIBLE" or "RETURN_WINDOW_EXPIRED"
                or "ITEM_HAS_NO_REFUNDABLE_AMOUNT"
                or "INVALID_STATUS_TRANSITION" or "INVALID_PAYMENT_STATE"
                or "DECISION_NOTE_REQUIRED" or "EVIDENCE_REFERENCE_REQUIRED"
                or "RECEIVED_EVIDENCE_REQUIRED" or "TRANSACTION_NOT_FOUND" => 400,
            _ => failure.Status != 0 ? failure.Status : 400
        };

        var title = failure.Message.Length > 0 ? failure.Message : "Yêu cầu không thể thực hiện.";
        await AuthProblems.Write(context, status, failure.Code, title);
        return true;
    }
}

