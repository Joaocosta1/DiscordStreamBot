namespace Bot.Domain.Entities;

public class StreamSession
{
    public string ServerId { get; private set; }
    public string TextChannelId { get; private set; }
    public string VoiceChannelId { get; private set; }

    public StreamSession(string serverId, string textChannelId, string voiceChannelId)
    {
        ServerId = serverId;
        TextChannelId = textChannelId;
        VoiceChannelId = voiceChannelId;
    }
}