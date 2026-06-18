namespace Bot.Infrastructure.Services;

using Microsoft.Playwright;
using Bot.Application.Interfaces;

public class PlaywrightDiscordService : IBrowserAutomationService
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _discordPage;
    private readonly string _sessionFile = "discord_session.json";

    public async Task StartBrowserAsync()
    {
        _playwright = await Playwright.CreateAsync();

        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = false,
            Args = new[]
            {
                "--auto-select-desktop-capture-source=YouTube",
                "--disable-user-media-security",
                "--mute-audio"
            }
        });

        var contextOptions = new BrowserNewContextOptions
        {
            Permissions = new[] { "microphone", "camera" }
        };

        if (File.Exists(_sessionFile))
        {
            contextOptions.StorageStatePath = _sessionFile;
            Console.WriteLine("[INFO] Sessão salva detectada. Tentando carregar...");
        }

        _context = await _browser.NewContextAsync(contextOptions);
    }

    public async Task LoginToDiscordAsync(string email, string password)
    {
        _discordPage = await _context!.NewPageAsync();
        Console.WriteLine("[INFO] Acessando tela de login...");
        await _discordPage.GotoAsync("https://discord.com/login");

        var emailInput = _discordPage.Locator("input[name='email']");
        if (await emailInput.IsVisibleAsync(new() { Timeout = 10000 }))
        {
            await emailInput.FillAsync(email);
            await _discordPage.FillAsync("input[name='password']", password);
            await _discordPage.ClickAsync("button[type='submit']");

            Console.WriteLine("[INFO] Aguardando autenticação...");
            await _discordPage.WaitForURLAsync("**/channels/**", new PageWaitForURLOptions { Timeout = 60000 });
            await _context.StorageStateAsync(new BrowserContextStorageStateOptions { Path = _sessionFile });
            Console.WriteLine("[INFO] Login realizado e sessão salva com sucesso.");
        }
    }

    public async Task ListenForCommandsAndStreamAsync(string serverId, string textChannelId, string voiceChannelId)
    {
        var targetUrl = $"https://discord.com/channels/{serverId}/{textChannelId}";
        Console.WriteLine($"[INFO] Navegando para: {targetUrl}");

        await _discordPage!.GotoAsync(targetUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60000 });

        var continueLink = _discordPage.Locator("text=Continuar no Navegador");
        if (await continueLink.IsVisibleAsync(new() { Timeout = 5000 }))
        {
            Console.WriteLine("[INFO] Modal detectada, clicando...");
            await continueLink.ClickAsync();
            await _discordPage.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }

        Console.WriteLine("[INFO] Aguardando carregamento do chat...");
        try
        {
            await _discordPage.WaitForSelectorAsync("div[data-list-item-id^='chat-messages']", new() { Timeout = 45000 });
        }
        catch (TimeoutException)
        {
            await _discordPage.ScreenshotAsync(new() { Path = "debug_error.png" });
            Console.WriteLine("[ERRO] Chat não carregou. Screenshot salva como 'debug_error.png'.");
            throw;
        }

        Console.WriteLine("[INFO] Sucesso! Chat carregado. Monitorando...");
        string lastMessageId = "";

        while (true)
        {
            var messageLocators = _discordPage.Locator("div[data-list-item-id^='chat-messages']");
            int count = await messageLocators.CountAsync();

            if (count > 0)
            {
                var lastMessage = messageLocators.Nth(count - 1);
                var messageId = await lastMessage.GetAttributeAsync("data-list-item-id");

                if (messageId != null && messageId != lastMessageId)
                {
                    lastMessageId = messageId;
                    var textContent = await lastMessage.Locator("div[class*='messageContent']").InnerTextAsync();

                    if (textContent.StartsWith("!play "))
                    {
                        string youtubeUrl = textContent.Substring(6).Trim();
                        Console.WriteLine($"[COMANDO] Recebido: {youtubeUrl}");
                        await StartStreamingAsync(serverId, voiceChannelId, youtubeUrl);
                    }
                }
            }
            await Task.Delay(2000);
        }
    }

    private async Task StartStreamingAsync(string serverId, string voiceChannelId, string youtubeUrl)
    {
        Console.WriteLine("[INFO] Entrando no canal de voz...");
        await _discordPage!.GotoAsync($"https://discord.com/channels/{serverId}/{voiceChannelId}");
        await Task.Delay(3000);

        var joinVoiceButton = _discordPage.GetByRole(AriaRole.Button, new() { Name = "Entrar na chamada de voz" });

        if (await joinVoiceButton.IsVisibleAsync(new() { Timeout = 5000 }))
        {
            await joinVoiceButton.ClickAsync();
            Console.WriteLine("[INFO] Clique realizado no botão de entrar.");
            await Task.Delay(2000);
        }
        else
        {
            Console.WriteLine("[INFO] Botão de entrar não encontrado (pode já estar conectado).");
        }

        var youtubePage = await _context!.NewPageAsync();
        await youtubePage.GotoAsync(youtubeUrl);
        await youtubePage.ClickAsync(".ytp-play-button", new PageClickOptions { Timeout = 5000 }).ContinueWith(t => { });

        await _discordPage.BringToFrontAsync();

        try
        {
            var shareButton = _discordPage.GetByLabel("Compartilhar sua tela");
            await shareButton.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
            await shareButton.ClickAsync();
            Console.WriteLine("[INFO] Transmissão iniciada.");
        }
        catch (Exception ex)
        {
            await _discordPage.ScreenshotAsync(new() { Path = "erro_final_share.png" });
            Console.WriteLine($"[ERRO] Falha ao compartilhar: {ex.Message}");
        }
    }
}