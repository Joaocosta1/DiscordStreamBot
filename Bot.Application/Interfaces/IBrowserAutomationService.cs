namespace Bot.Application.Interfaces;

public interface IBrowserAutomationService
{
    Task StartBrowserAsync();
    Task LoginToDiscordAsync(string email, string password);
    Task ListenForCommandsAndStreamAsync(string serverId, string textChannelId, string voiceChannelId);
}