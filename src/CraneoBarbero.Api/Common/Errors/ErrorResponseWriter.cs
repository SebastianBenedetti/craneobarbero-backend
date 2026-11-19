using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace CraneoBarbero.Api.Common.Errors;

public static class ErrorResponseWriter
{
    public static async Task WriteAsync(HttpContext context, int statusCode, ApiErrorResponse body)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        var options = context.RequestServices.GetService<IOptions<JsonOptions>>()?.Value.SerializerOptions
                      ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await JsonSerializer.SerializeAsync(context.Response.Body, body, options, context.RequestAborted);
    }

    public static ApiErrorResponse ForStatusCode(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => new(ErrorMessages.BadRequest, ErrorCodes.BadRequest),
        StatusCodes.Status401Unauthorized => new(ErrorMessages.Unauthorized, ErrorCodes.Unauthorized),
        StatusCodes.Status403Forbidden => new(ErrorMessages.Forbidden, ErrorCodes.Forbidden),
        StatusCodes.Status404NotFound => new(ErrorMessages.NotFound, ErrorCodes.NotFound),
        StatusCodes.Status405MethodNotAllowed => new(ErrorMessages.MethodNotAllowed, ErrorCodes.MethodNotAllowed),
        StatusCodes.Status409Conflict => new(ErrorMessages.Conflict, ErrorCodes.Conflict),
        StatusCodes.Status413PayloadTooLarge => new(ErrorMessages.PayloadTooLarge, ErrorCodes.PayloadTooLarge),
        StatusCodes.Status415UnsupportedMediaType => new(ErrorMessages.UnsupportedMediaType, ErrorCodes.UnsupportedMediaType),
        StatusCodes.Status429TooManyRequests => new(ErrorMessages.TooManyRequests, ErrorCodes.TooManyRequests),
        >= 500 => new(ErrorMessages.InternalError, ErrorCodes.InternalError),
        _ => new(ErrorMessages.BadRequest, ErrorCodes.BadRequest),
    };
}
