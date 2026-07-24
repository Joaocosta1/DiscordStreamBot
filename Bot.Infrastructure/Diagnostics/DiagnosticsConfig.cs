namespace Bot.Infrastructure.Diagnostics;

using System.Diagnostics;

/// <summary>
/// Nomes e fontes de telemetria compartilhados entre a instrumentação (Infrastructure)
/// e a configuração do OpenTelemetry (Console). O <see cref="ActivitySource"/> emite
/// spans de tracing das operações de automação do browser.
/// </summary>
public static class DiagnosticsConfig
{
    public const string ServiceName = "DiscordStreamBot";

    public static readonly ActivitySource ActivitySource = new(ServiceName);
}
