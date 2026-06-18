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

    public async Task ExecuteAsync(string email, string password, StreamSession session)
    {
        await _browserService.StartBrowserAsync();
        await _browserService.LoginToDiscordAsync(email, password);
        await _browserService.ListenForCommandsAndStreamAsync(session.ServerId, session.TextChannelId, session.VoiceChannelId);
    }
}