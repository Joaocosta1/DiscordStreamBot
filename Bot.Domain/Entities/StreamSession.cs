namespace Bot.Domain.Entities;

/// <summary>
/// Value object imutável com os identificadores do Discord alvo da transmissão.
/// Valida na construção — nenhum identificador pode ser nulo ou vazio.
/// </summary>
public sealed record StreamSession
{
    public string ServerId { get; }
    public string TextChannelId { get; }
    public string VoiceChannelId { get; }

    public StreamSession(string serverId, string textChannelId, string voiceChannelId)
    {
        ServerId = Require(serverId, nameof(serverId));
        TextChannelId = Require(textChannelId, nameof(textChannelId));
        VoiceChannelId = Require(voiceChannelId, nameof(voiceChannelId));
    }

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("O identificador não pode ser nulo ou vazio.", name)
            : value;
}
