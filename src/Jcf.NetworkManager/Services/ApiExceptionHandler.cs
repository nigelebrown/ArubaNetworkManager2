using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;

namespace Jcf.NetworkManager.Services;

/// <summary>
/// Maps the application's domain exceptions to RFC 7807 responses instead of HTTP 500.
/// Only messages thrown by JCF code are returned to the caller; framework or unexpected
/// exceptions return a generic message so internal details are not disclosed.
/// </summary>
public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, title) = exception switch
        {
            KeyNotFoundException => (StatusCodes.Status404NotFound, "Not found"),
            UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "Not permitted"),
            ArgumentException or FormatException => (StatusCodes.Status400BadRequest, "Invalid request"),
            InvalidOperationException => (StatusCodes.Status409Conflict, "Request conflicts with policy or current state"),
            NotSupportedException => (StatusCodes.Status400BadRequest, "Not supported"),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected server error")
        };
        // Framework request-validation failures are client errors with a safe, fixed message.
        if (exception is Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException)
            return await WriteAsync(context, StatusCodes.Status400BadRequest, "Invalid request", "The form or request anti-forgery token is missing or invalid. Reload the page and try again.", ct);
        if (exception is BadHttpRequestException bad)
            return await WriteAsync(context, bad.StatusCode, "Invalid request", "The request was malformed.", ct);
        var fromApplication = status != StatusCodes.Status500InternalServerError && ThrownByApplication(exception);
        if (!fromApplication)
        {
            status = StatusCodes.Status500InternalServerError;
            title = "Unexpected server error";
            log.LogError(exception, "Unhandled request failure on {Path}", context.Request.Path);
        }
        else log.LogInformation("Request rejected on {Path}: {Kind}", context.Request.Path, exception.GetType().Name);

        return await WriteAsync(context, status, title,
            fromApplication ? exception.Message : "The request could not be completed. The error has been logged.", ct);
    }

    private static async Task<bool> WriteAsync(HttpContext context, int status, string title, string detail, CancellationToken ct)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new
        {
            type = "about:blank",
            title,
            status,
            detail,
            traceId = Activity.Current?.Id ?? context.TraceIdentifier
        }, options: null, contentType: "application/problem+json", cancellationToken: ct);
        return true;
    }

    private static bool ThrownByApplication(Exception exception)
    {
        foreach (var frame in new StackTrace(exception).GetFrames())
        {
            var type = frame.GetMethod()?.DeclaringType;
            if (type is null) continue;
            var name = type.FullName ?? "";
            return name.StartsWith("Jcf.", StringComparison.Ordinal) || name.StartsWith("Program", StringComparison.Ordinal);
        }
        return false;
    }
}
