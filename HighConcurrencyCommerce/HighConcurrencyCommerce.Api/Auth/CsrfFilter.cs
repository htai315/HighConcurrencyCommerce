using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace HighConcurrencyCommerce.Api.Auth;

public sealed class CsrfFilter(IAntiforgery antiforgery) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var method = context.HttpContext.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)) return;
        try { await antiforgery.ValidateRequestAsync(context.HttpContext); }
        catch (AntiforgeryValidationException)
        {
            var problem = new ProblemDetails { Status = 400, Title = "Phiên bảo vệ đã thay đổi. Vui lòng thử lại." };
            problem.Extensions["code"] = "CSRF_INVALID";
            problem.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;
            context.Result = new BadRequestObjectResult(problem);
        }
    }
}

