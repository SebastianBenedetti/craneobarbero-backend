namespace CraneoBarbero.Api.Security;

public sealed class SecurityHeadersMiddleware(RequestDelegate next, IWebHostEnvironment environment)
{
    private static readonly string[] DocumentationPaths = ["/scalar", "/openapi"];

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-site";

        if (!IsDocumentation(context.Request.Path))
        {
            headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";
            headers.CacheControl = "no-store";
        }

        return next(context);
    }

    private bool IsDocumentation(PathString path) =>
        environment.IsDevelopment() &&
        DocumentationPaths.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
}
