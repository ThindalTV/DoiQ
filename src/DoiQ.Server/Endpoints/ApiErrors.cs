using DoiQ.Core.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace DoiQ.Server.Endpoints;

/// <summary>Maps TaskService exceptions to HTTP: 400 validation, 401, 403 permission rule, 404, 409 stale version/duplicate.</summary>
public sealed class DomainExceptionHandler(ILogger<DomainExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        var (status, title) = ex switch
        {
            ValidationException => (400, "Validation failed"),
            UnauthorizedException => (401, "Unauthorized"),
            ForbiddenException => (403, "Forbidden"),
            NotFoundException => (404, "Not found"),
            ConflictException => (409, "Conflict"),
            BadHttpRequestException => (400, "Bad request"),
            _ => (0, ""),
        };
        if (status == 0)
        {
            logger.LogError(ex, "Unhandled exception");
            return false;
        }
        ctx.Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status, Title = title, Detail = ex.Message };
        if (ex is StaleVersionException stale)
        {
            problem.Extensions["code"] = "stale";
            problem.Extensions["taskId"] = stale.TaskId;
            problem.Extensions["currentVersion"] = stale.CurrentVersion;
            problem.Extensions["changedBy"] = stale.ChangedBy;
        }
        if (ex is BoardNotEmptyException notEmpty)
        {
            problem.Extensions["code"] = "board-not-empty";
            problem.Extensions["openTasks"] = notEmpty.OpenTasks;
            problem.Extensions["totalTasks"] = notEmpty.TotalTasks;
        }
        await ctx.Response.WriteAsJsonAsync(problem, ct);
        return true;
    }
}
