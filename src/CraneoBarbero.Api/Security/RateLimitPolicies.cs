using System.Globalization;
using System.Threading.RateLimiting;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Configuration;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace CraneoBarbero.Api.Security;

public static class RateLimitPolicies
{
    public const string Auth = "auth";

    public static IServiceCollection AddApplicationRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>()
            .Configure<IOptions<SecuritySettings>>((options, security) =>
            {
                var global = security.Value.GlobalRateLimit;
                var auth = security.Value.AuthRateLimit;

                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                    RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => CreateOptions(global)));

                options.AddPolicy(Auth, context =>
                    RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => CreateOptions(auth)));

                options.OnRejected = async (context, cancellationToken) =>
                {
                    if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    {
                        context.HttpContext.Response.Headers.RetryAfter =
                            ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                    }

                    await ErrorResponseWriter.WriteAsync(context.HttpContext, StatusCodes.Status429TooManyRequests,
                        ErrorResponseWriter.ForStatusCode(StatusCodes.Status429TooManyRequests));
                };
            });

        return services;
    }

    private static FixedWindowRateLimiterOptions CreateOptions(RateLimitSettings settings) => new()
    {
        PermitLimit = settings.PermitLimit,
        Window = TimeSpan.FromSeconds(settings.WindowSeconds),
        QueueLimit = 0,
        AutoReplenishment = true,
    };

    private static string ClientKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
