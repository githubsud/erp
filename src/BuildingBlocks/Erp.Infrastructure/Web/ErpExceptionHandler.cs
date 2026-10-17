using Erp.Infrastructure.Persistence;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Erp.Infrastructure.Web;

/// <summary>Maps expected failures to RFC 7807 problem details with bilingual messages (<c>message_ar</c>).</summary>
public sealed class ErpExceptionHandler(IProblemDetailsService problemDetails, ILogger<ErpExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, message, messageAr, details) = exception switch
        {
            NotFoundException e => (StatusCodes.Status404NotFound, e.Code, e.Message, e.MessageAr, e.Details),
            ForbiddenException e => (StatusCodes.Status403Forbidden, e.Code, e.Message, e.MessageAr, e.Details),
            ConflictException e => (StatusCodes.Status409Conflict, e.Code, e.Message, e.MessageAr, e.Details),
            BusinessRuleException e => (StatusCodes.Status422UnprocessableEntity, e.Code, e.Message, e.MessageAr, e.Details),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "concurrency",
                "The record was changed by someone else. Reload and try again.", "تم تعديل السجل من قبل مستخدم آخر. أعد التحميل وحاول مرة أخرى.", null),
            BadHttpRequestException e => (StatusCodes.Status400BadRequest, "bad_request", e.Message, null, null),
            _ when PostgresErrors.Find(exception) is { SqlState: PostgresErrors.UniqueViolation } pg =>
                (StatusCodes.Status409Conflict, "duplicate", $"A record with the same key already exists ({pg.ConstraintName}).",
                    "يوجد سجل بنفس المفتاح مسبقاً.", null),
            _ => (0, "", "", null, (IReadOnlyDictionary<string, object?>?)null)
        };

        if (status == 0)
        {
            logger.LogError(exception, "Unhandled exception");
            return false;
        }

        httpContext.Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status, Title = message, Type = $"https://erp.qa/errors/{code}" };
        problem.Extensions["code"] = code;
        if (messageAr is not null) problem.Extensions["message_ar"] = messageAr;
        if (details is { Count: > 0 }) problem.Extensions["details"] = details;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception
        });
    }
}
