namespace Bot.Console;

using Bot.Application.UseCases;
using Bot.Domain.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Serviço hospedado que carrega a configuração e executa a automação até o
/// cancelamento (Ctrl+C). Ao terminar/erro, encerra a aplicação para que o host
/// descarte os serviços (fechando o browser via <c>IAsyncDisposable</c>).
/// </summary>
public sealed class StreamingWorker : BackgroundService
{
    private const string ConfigPath = "config.txt";

    private static readonly string[] RequiredKeys =
        { "EMAIL", "SENHA", "GUILD_ID", "TEXT_CHANNEL_ID", "VOICE_CHANNEL_ID" };

    private readonly StartStreamingUseCase _useCase;
    private readonly ILogger<StreamingWorker> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    public StreamingWorker(
        StartStreamingUseCase useCase,
        ILogger<StreamingWorker> logger,
        IHostApplicationLifetime lifetime)
    {
        _useCase = useCase;
        _logger = logger;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("=== Discord Stream Bot (Playwright & .NET 10) ===");
        try
        {
            var (email, password, session) = await LoadConfigurationAsync();
            _logger.LogInformation("Inicializando automação...");
            await _useCase.ExecuteAsync(email, password, session, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Encerramento solicitado. Finalizando...");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Erro crítico ao executar a automação.");
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }

    private async Task<(string Email, string Password, StreamSession Session)> LoadConfigurationAsync()
    {
        if (File.Exists(ConfigPath))
        {
            _logger.LogInformation("Lendo configurações de {ConfigPath}...", ConfigPath);
            var lines = await File.ReadAllLinesAsync(ConfigPath);
            var config = lines.Select(l => l.Split('=', 2))
                              .Where(s => s.Length == 2)
                              .ToDictionary(s => s[0].Trim(), s => s[1].Trim());

            var missing = RequiredKeys
                .Where(k => !config.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v))
                .ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Chaves obrigatórias ausentes ou vazias em {ConfigPath}: {string.Join(", ", missing)}");
            }

            return (
                config["EMAIL"],
                config["SENHA"],
                new StreamSession(config["GUILD_ID"], config["TEXT_CHANNEL_ID"], config["VOICE_CHANNEL_ID"]));
        }

        // Fallback interativo (prompts são a única exceção permitida ao Console.*)
        System.Console.Write("Seu Email do Discord: "); var email = System.Console.ReadLine() ?? "";
        System.Console.Write("Sua Senha: "); var password = System.Console.ReadLine() ?? "";
        System.Console.Write("ID do Servidor (Guild ID): "); var serverId = System.Console.ReadLine() ?? "";
        System.Console.Write("ID do Canal de Texto (!play): "); var textChannelId = System.Console.ReadLine() ?? "";
        System.Console.Write("ID do Canal de Voz: "); var voiceChannelId = System.Console.ReadLine() ?? "";

        return (email, password, new StreamSession(serverId, textChannelId, voiceChannelId));
    }
}
