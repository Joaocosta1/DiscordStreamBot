using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Bot.Application.Interfaces;
using Bot.Application.UseCases;
using Bot.Infrastructure.Services;
using Bot.Domain.Entities;

var builder = Host.CreateDefaultBuilder(args);
builder.ConfigureServices((context, services) =>
{
    services.AddSingleton<IBrowserAutomationService, PlaywrightDiscordService>();
    services.AddTransient<StartStreamingUseCase>();
});

var app = builder.Build();
var streamingUseCase = app.Services.GetRequiredService<StartStreamingUseCase>();

Console.WriteLine("=== Discord Stream Bot (Playwright & .NET 10) ===");

string email, password, serverId, textChannelId, voiceChannelId;

string configPath = "config.txt";
if (File.Exists(configPath))
{
    Console.WriteLine("[INFO] Lendo configurações de config.txt...");
    var lines = await File.ReadAllLinesAsync(configPath);
    var config = lines.Select(l => l.Split('='))
                      .Where(s => s.Length == 2)
                      .ToDictionary(s => s[0].Trim(), s => s[1].Trim());

    email = config["EMAIL"];
    password = config["SENHA"];
    serverId = config["GUILD_ID"];
    textChannelId = config["TEXT_CHANNEL_ID"];
    voiceChannelId = config["VOICE_CHANNEL_ID"];
}
else
{
    // Fallback para input manual se o arquivo não existir
    Console.Write("Seu Email do Discord: "); email = Console.ReadLine()!;
    Console.Write("Sua Senha: "); password = Console.ReadLine()!;
    Console.Write("ID do Servidor (Guild ID): "); serverId = Console.ReadLine()!;
    Console.Write("ID do Canal de Texto (!play): "); textChannelId = Console.ReadLine()!;
    Console.Write("ID do Canal de Voz: "); voiceChannelId = Console.ReadLine()!;
}

Console.WriteLine("\n[INFO] Inicializando automação...");
try
{
    var session = new StreamSession(serverId, textChannelId, voiceChannelId);
    await streamingUseCase.ExecuteAsync(email, password, session);
}
catch (Exception ex)
{
    Console.WriteLine($"[ERRO CRÍTICO]: {ex.Message}");
}