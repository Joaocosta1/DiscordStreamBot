namespace Bot.Application.UseCases;

using Bot.Application.Interfaces;
using Bot.Domain.Entities;

public class StartStreamingUseCase
{
    private readonly IBrowserAutomationService _browserService;

    public StartStreamingUseCase(IBrowserAutomationService browserService)
    {
        _browserService = browserService;
    }

    public async Task ExecuteAsync(string email, string password, StreamSession session, CancellationToken cancellationToken = default)
    {
        await _browserService.StartBrowserAsync(cancellationToken);
        await _browserService.LoginToDiscordAsync(email, password, cancellationToken);
        await _browserService.ListenForCommandsAndStreamAsync(session.ServerId, session.TextChannelId, session.VoiceChannelId, cancellationToken);
    }
}
