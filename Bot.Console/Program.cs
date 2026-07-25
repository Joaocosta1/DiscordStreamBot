using Bot.Application.Interfaces;
using Bot.Application.UseCases;
using Bot.Console;
using Bot.Infrastructure.Diagnostics;
using Bot.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = Host.CreateDefaultBuilder(args);

builder.ConfigureServices((context, services) =>
{
    services.AddSingleton<IBrowserAutomationService, PlaywrightDiscordService>();
    services.AddTransient<StartStreamingUseCase>();
    services.AddHostedService<StreamingWorker>();

    services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(DiagnosticsConfig.ServiceName))
        .WithTracing(tracing => tracing
            .AddSource(DiagnosticsConfig.ServiceName)
            .AddConsoleExporter())
        .WithMetrics(metrics => metrics
            .AddRuntimeInstrumentation()
            .AddConsoleExporter());
});

builder.ConfigureLogging((context, logging) =>
{
    logging.ClearProviders();
    logging.AddOpenTelemetry(options =>
    {
        options.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(DiagnosticsConfig.ServiceName));
        options.IncludeScopes = true;
        options.IncludeFormattedMessage = true;
        options.AddConsoleExporter();
    });
});

await builder.Build().RunAsync();
