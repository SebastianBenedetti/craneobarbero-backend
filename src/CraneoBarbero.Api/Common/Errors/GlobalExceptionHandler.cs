using Microsoft.AspNetCore.Diagnostics;

namespace CraneoBarbero.Api.Common.Errors;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        switch (exception)
        {
            case AppException appException:
                await ErrorResponseWriter.WriteAsync(context, appException.StatusCode,
                    new ApiErrorResponse(appException.Message, appException.Code, appException.Errors));
                return true;

            case BadHttpRequestException badRequest:
                logger.LogWarning("Solicitud rechazada con estado {StatusCode}", badRequest.StatusCode);
                await ErrorResponseWriter.WriteAsync(context, badRequest.StatusCode,
                    ErrorResponseWriter.ForStatusCode(badRequest.StatusCode));
                return true;

            case OperationCanceledException when context.RequestAborted.IsCancellationRequested:
                return true;

            default:
                logger.LogError(exception, "Error no controlado en {Method} {Path}", context.Request.Method, context.Request.Path);
                await ErrorResponseWriter.WriteAsync(context, StatusCodes.Status500InternalServerError,
                    ErrorResponseWriter.ForStatusCode(StatusCodes.Status500InternalServerError));
                return true;
        }
    }
}
