using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CraneoBarbero.Api.Accounts;
using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Common.Validation;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Data.Import;
using CraneoBarbero.Api.Data.Seeding;
using CraneoBarbero.Api.Email;
using CraneoBarbero.Api.Security;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
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

        services.AddSingleton<TimestampsInterceptor>();
        services.AddDbContext<AppDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<IOptions<DatabaseSettings>>().Value.ConnectionString,
                    DatabaseSetup.ConfigureNpgsql)
                .AddInterceptors(provider.GetRequiredService<TimestampsInterceptor>()));

        services.AddScoped<AdminSeeder>();
        services.AddHostedService<AdminSeedHostedService>();
        services.AddScoped<LegacyImporter>();
        services.AddScoped<Catalog.FutureAppointments>();
        services.AddScoped<Store.StockLedger>();
        services.AddScoped<Pricing.PriceHistoryWriter>();
        services.AddScoped<Store.OrderTransitions>();
        services.AddScoped<Store.OrderManagement>();
        services.AddScoped<Payments.OrderPayments>();
        services.AddScoped<Schedule.OpeningSchedule>();
        services.AddScoped<Schedule.SlotGenerator>();
        services.AddScoped<Schedule.AvailabilityService>();
        services.AddScoped<Schedule.HolidayService>();
        services.AddScoped<Schedule.ScheduleManagement>();
        services.AddScoped<Appointments.AppointmentBooking>();
        services.AddScoped<Appointments.AppointmentChanges>();
        services.AddScoped<Accounts.AccountClosure>();
        services.AddScoped<Payments.AppointmentPayments>();
        services.AddScoped<Appointments.IAppointmentRefunds>(provider => provider.GetRequiredService<Payments.AppointmentPayments>());
        services.AddOptions<Payments.MercadoPagoSettings>()
            .BindConfiguration(Payments.MercadoPagoSettings.SectionName)
            .Configure<IConfiguration>((settings, configuration) =>
            {
                settings.AccessToken = configuration[EnvironmentVariables.MercadoPagoAccessToken]?.Trim() ?? string.Empty;
                settings.WebhookSecret = configuration[EnvironmentVariables.MercadoPagoWebhookSecret]?.Trim() ?? string.Empty;
                var notification = configuration[EnvironmentVariables.MercadoPagoNotificationUrl];
                var external = configuration[EnvironmentVariables.RenderExternalUrl];
                settings.NotificationUrl = !string.IsNullOrWhiteSpace(notification)
                    ? notification.Trim()
                    : !string.IsNullOrWhiteSpace(external) ? external.Trim().TrimEnd('/') + "/payments/webhook" : string.Empty;
                var frontend = CorsSettings.ParseOrigins(configuration[EnvironmentVariables.FrontendUrl]).FirstOrDefault();
                settings.ReturnUrl = frontend is null ? string.Empty : frontend.TrimEnd('/') + "/payments/return";
            });
        services.AddHttpClient<Payments.IPaymentGateway, Payments.MercadoPagoClient>((provider, client) =>
        {
            var settings = provider.GetRequiredService<IOptions<Payments.MercadoPagoSettings>>().Value;
            client.BaseAddress = new Uri(settings.ApiUrl);
            client.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
        });
        services.AddScoped<Appointments.AppointmentLifecycle>();
        services.AddHostedService<Appointments.AppointmentJobsService>();
        services.AddOptions<Schedule.ScheduleSettings>().BindConfiguration(Schedule.ScheduleSettings.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<Schedule.ScheduleSettings>, Schedule.ScheduleSettingsValidator>();
        services.AddHttpClient<Schedule.IHolidaySource, Schedule.ArgentinaDatosHolidaySource>((provider, client) =>
        {
            var settings = provider.GetRequiredService<IOptions<Schedule.ScheduleSettings>>().Value;
            client.BaseAddress = new Uri(settings.HolidaysApiUrl);
            client.Timeout = TimeSpan.FromSeconds(settings.HolidaysApiTimeoutSeconds);
        });
        services.AddHostedService<Schedule.ScheduleJobsService>();

        return services;
    }

    public static IServiceCollection AddApplicationEmail(this IServiceCollection services)
    {
        services.AddOptions<EmailSettings>()
            .BindConfiguration(EmailSettings.SectionName)
            .Configure<IConfiguration>((settings, configuration) =>
            {
                var apiKey = configuration[EnvironmentVariables.BrevoApiKey];
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    settings.BrevoApiKey = apiKey.Trim();
                }

                var from = configuration[EnvironmentVariables.EmailFrom];
                if (!string.IsNullOrWhiteSpace(from))
                {
                    settings.FromEmail = from.Trim();
                }

                var fromName = configuration[EnvironmentVariables.EmailFromName];
                if (!string.IsNullOrWhiteSpace(fromName))
                {
                    settings.FromName = fromName.Trim();
                }

                settings.FrontendBaseUrl =
                    CorsSettings.ParseOrigins(configuration[EnvironmentVariables.FrontendUrl]).FirstOrDefault() ?? string.Empty;

                var external = configuration[EnvironmentVariables.RenderExternalUrl];
                if (!string.IsNullOrWhiteSpace(external))
                {
                    settings.ApiBaseUrl = external.Trim().TrimEnd('/');
                }
            });

        services.AddHttpClient<IEmailSender, BrevoEmailSender>((provider, client) =>
        {
            var settings = provider.GetRequiredService<IOptions<EmailSettings>>().Value;
            client.BaseAddress = new Uri(settings.BrevoApiUrl);
            client.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
        });

        services.AddSingleton<EmailQueue>();
        services.AddSingleton<IEmailQueue>(provider => provider.GetRequiredService<EmailQueue>());
        services.AddHostedService<EmailDispatcher>();
        services.AddScoped<AccountEmailService>();
        services.AddSingleton<Calendar.AppointmentCalendar>();
        services.AddSingleton<Calendar.CalendarLinks>();
        services.AddScoped<Appointments.AppointmentNotifications>();
        services.AddScoped<Appointments.AppointmentReminders>();
        services.AddScoped<AuthTokenService>();

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
                options.ModelBinderProviders.Insert(0, new Common.Validation.SnakeCaseEnumModelBinderProvider());
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
        services.AddDataProtection().UseEphemeralDataProtectionProvider();

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
