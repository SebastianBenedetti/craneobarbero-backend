using System.Globalization;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Extensions;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);
builder.AddDotEnvForDevelopment();

if (int.TryParse(builder.Configuration[EnvironmentVariables.Port], NumberStyles.None,
        CultureInfo.InvariantCulture, out var port) && port > 0)
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

builder.WebHost.ConfigureKestrel((context, kestrel) =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize =
        context.Configuration.GetValue<long?>($"{SecuritySettings.SectionName}:MaxRequestBodyBytes") ?? 1_048_576;
});

builder.Services.AddSerilog((services, logger) =>
{
    logger.ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext();

    if (builder.Environment.IsDevelopment())
    {
        logger.WriteTo.Console(formatProvider: CultureInfo.InvariantCulture);
    }
    else
    {
        logger.WriteTo.Console(new RenderedCompactJsonFormatter());
    }
});

builder.Services
    .AddApplicationSettings()
    .AddApplicationData()
    .AddApplicationControllers()
    .AddApplicationAuthentication()
    .AddApplicationSecurity()
    .AddApplicationDocumentation();

var app = builder.Build();

app.UseApplicationPipeline();

app.Run();

public partial class Program;
