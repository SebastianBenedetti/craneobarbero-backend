using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Security;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using Serilog;

namespace CraneoBarbero.Api.Extensions;

public static class WebApplicationExtensions
{
    public static WebApplication UseApplicationPipeline(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseSerilogRequestLogging(options =>
        {
            options.EnrichDiagnosticContext = (diagnostics, context) =>
            {
                var userId = context.User.GetUserId();
                if (userId is not null)
                {
                    diagnostics.Set("UserId", userId.Value);
                }
            };
        });

        app.UseExceptionHandler();
        app.UseStatusCodePages(context =>
        {
            var response = context.HttpContext.Response;
            if (response.ContentLength is not null || !string.IsNullOrEmpty(response.ContentType))
            {
                return Task.CompletedTask;
            }

            return ErrorResponseWriter.WriteAsync(context.HttpContext, response.StatusCode,
                ErrorResponseWriter.ForStatusCode(response.StatusCode));
        });

        app.UseMiddleware<SecurityHeadersMiddleware>();
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.UseMiddleware<RequestBodyLimitMiddleware>();
        app.UseRouting();
        app.UseCors(CorsSettings.PolicyName);
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapControllers().RequireAuthorization();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi().AllowAnonymous();
            app.MapScalarApiReference().AllowAnonymous();
        }

        if (CorsSettings.ParseOrigins(app.Configuration[EnvironmentVariables.FrontendUrl]).Length == 0)
        {
            app.Logger.LogWarning("{Variable} no tiene un origen válido: el navegador no podrá llamar a la API.",
                EnvironmentVariables.FrontendUrl);
        }

        if (!app.Services.GetRequiredService<IOptions<EmailSettings>>().Value.IsConfigured)
        {
            app.Logger.LogWarning("Faltan {ApiKey} o {From}: los mails no se van a enviar.",
                EnvironmentVariables.BrevoApiKey, EnvironmentVariables.EmailFrom);
        }

        return app;
    }
}
