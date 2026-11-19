using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Configuration;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace CraneoBarbero.Api.Security;

public sealed class RequestBodyLimitMiddleware(RequestDelegate next, IOptions<SecuritySettings> settings)
{
    public Task InvokeAsync(HttpContext context)
    {
        var limit = settings.Value.MaxRequestBodyBytes;

        if (context.Request.ContentLength > limit)
        {
            return ErrorResponseWriter.WriteAsync(context, StatusCodes.Status413PayloadTooLarge,
                ErrorResponseWriter.ForStatusCode(StatusCodes.Status413PayloadTooLarge));
        }

        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = limit;
        }

        return next(context);
    }
}
