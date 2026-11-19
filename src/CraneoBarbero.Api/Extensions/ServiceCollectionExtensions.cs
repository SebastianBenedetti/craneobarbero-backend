using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Common.Validation;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Security;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace CraneoBarbero.Api.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationSettings(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);

        services.AddOptions<BusinessSettings>().BindConfiguration(BusinessSettings.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<BusinessSettings>, BusinessSettingsValidator>();
        services.AddSingleton<BusinessClock>();

        services.AddOptions<JwtSettings>()
            .BindConfiguration(JwtSettings.SectionName)
            .Configure<IConfiguration>((settings, configuration) =>
            {
                var secret = configuration[EnvironmentVariables.JwtSecret];
                if (!string.IsNullOrEmpty(secret))
                {
                    settings.Secret = secret;
                }
            })
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<JwtSettings>, JwtSettingsValidator>();

        services.AddOptions<SecuritySettings>().BindConfiguration(SecuritySettings.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<SecuritySettings>, SecuritySettingsValidator>();

        return services;
    }

    public static IServiceCollection AddApplicationData(this IServiceCollection services)
    {
        services.AddOptions<DatabaseSettings>()
            .Configure<IConfiguration, IHostEnvironment>((settings, configuration, environment) =>
            {
                var certificate = configuration[EnvironmentVariables.DatabaseRootCertificate];
                if (string.IsNullOrWhiteSpace(certificate))
                {
                    certificate = configuration[$"{DatabaseSettings.SectionName}:RootCertificatePath"];
                }

                if (!string.IsNullOrWhiteSpace(certificate))
                {
                    certificate = Path.GetFullPath(certificate, environment.ContentRootPath);
                }

                settings.ConnectionString = DatabaseConnection.BuildConnectionString(
                    configuration[EnvironmentVariables.DatabaseUrl], certificate);
            })
            .ValidateOnStart();

        services.AddDbContext<AppDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<IOptions<DatabaseSettings>>().Value.ConnectionString));

        return services;
    }

    public static IServiceCollection AddApplicationControllers(this IServiceCollection services)
    {
        ValidatorOptions.Global.LanguageManager.Culture = new CultureInfo("es");
        services.AddValidatorsFromAssemblyContaining<Program>();

        services.AddControllers(options =>
            {
                options.Filters.Add<RouteIdValidationFilter>();
                options.Filters.Add<ValidationFilter>();
                options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;

                var messages = options.ModelBindingMessageProvider;
                messages.SetValueIsInvalidAccessor(_ => ErrorMessages.InvalidValue);
                messages.SetAttemptedValueIsInvalidAccessor((_, _) => ErrorMessages.InvalidValue);
                messages.SetNonPropertyAttemptedValueIsInvalidAccessor(_ => ErrorMessages.InvalidValue);
                messages.SetUnknownValueIsInvalidAccessor(_ => ErrorMessages.InvalidValue);
                messages.SetNonPropertyUnknownValueIsInvalidAccessor(() => ErrorMessages.InvalidValue);
                messages.SetValueMustBeANumberAccessor(_ => "El valor debe ser un número.");
                messages.SetNonPropertyValueMustBeANumberAccessor(() => "El valor debe ser un número.");
                messages.SetValueMustNotBeNullAccessor(_ => "El valor es obligatorio.");
                messages.SetMissingBindRequiredValueAccessor(_ => "Falta un valor obligatorio.");
                messages.SetMissingKeyOrValueAccessor(() => "Falta un valor obligatorio.");
                messages.SetMissingRequestBodyRequiredValueAccessor(() => ErrorMessages.MissingBody);
            })
            .ConfigureApiBehaviorOptions(options =>
            {
                options.InvalidModelStateResponseFactory = InvalidModelStateResponse.Create;
                options.SuppressMapClientErrors = true;
            })
            .AddJsonOptions(options => ConfigureJson(options.JsonSerializerOptions));

        services.ConfigureHttpJsonOptions(options => ConfigureJson(options.SerializerOptions));

        services.AddProblemDetails();
        services.AddExceptionHandler<GlobalExceptionHandler>();

        return services;
    }

    public static IServiceCollection AddApplicationAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.ConfigureOptions<JwtBearerSetup>();
        services.AddAuthorization(options => options.AddApplicationPolicies());

        services.AddSingleton<IAuthorizationHandler, ResourceOwnerHandler>();
        services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
        services.AddSingleton<IJwtTokenService, JwtTokenService>();
        services.AddScoped<IUserAccessStore, EfUserAccessStore>();

        return services;
    }

    public static IServiceCollection AddApplicationSecurity(this IServiceCollection services)
    {
        services.AddCors();
        services.AddOptions<CorsOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                var origins = CorsSettings.ParseOrigins(configuration[EnvironmentVariables.FrontendUrl]);
                options.AddPolicy(CorsSettings.PolicyName, policy =>
                {
                    policy.WithOrigins(origins)
                        .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
                        .WithHeaders("Authorization", "Content-Type")
                        .WithExposedHeaders("Retry-After", "Content-Disposition")
                        .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
                });
            });

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

        services.AddHsts(options =>
        {
            options.MaxAge = TimeSpan.FromDays(365);
            options.IncludeSubDomains = true;
        });

        services.AddApplicationRateLimiting();

        return services;
    }

    public static IServiceCollection AddApplicationDocumentation(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "Cráneo Barbero API";
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                };
                return Task.CompletedTask;
            });
        });

        return services;
    }

    private static void ConfigureJson(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
    }
}
