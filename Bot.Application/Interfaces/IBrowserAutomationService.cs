namespace Bot.Application.Interfaces;

/// <summary>
/// Contrato de automação do cliente web do Discord. Detém recursos do browser,
/// portanto é <see cref="IAsyncDisposable"/> — o container de DI o descarta no shutdown.
/// Ordem esperada de chamada: <see cref="StartBrowserAsync"/> → <see cref="LoginToDiscordAsync"/>
/// → <see cref="ListenForCommandsAndStreamAsync"/>.
/// </summary>
public interface IBrowserAutomationService : IAsyncDisposable
{
    Task StartBrowserAsync(CancellationToken cancellationToken = default);
    Task LoginToDiscordAsync(string email, string password, CancellationToken cancellationToken = default);
    Task ListenForCommandsAndStreamAsync(string serverId, string textChannelId, string voiceChannelId, CancellationToken cancellationToken = default);
}
