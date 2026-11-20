using HighConcurrencyCommerce.Api.Auth;
using HighConcurrencyCommerce.Application.CustomerOrders;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HighConcurrencyCommerce.Api.CustomerOrders;

public sealed class CustomerOrderExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is CustomerOrderFailure failure)
        {
            await AuthProblems.Write(context, failure.Status, failure.Code, failure.Message);
            return true;
        }

        if (exception is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure }
            || exception is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure } })
        {
            await AuthProblems.Write(context, 409, "CONCURRENT_CONFLICT", "Thao tác hủy đơn bị xung đột xử lý đồng thời. Vui lòng thử lại.");
            return true;
        }

        return false;
    }
}
