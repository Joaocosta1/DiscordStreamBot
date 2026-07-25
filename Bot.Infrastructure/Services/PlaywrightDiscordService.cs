namespace Bot.Infrastructure.Services;

using Microsoft.Playwright;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Bot.Application.Interfaces;
using Bot.Infrastructure.Diagnostics;

public class PlaywrightDiscordService : IBrowserAutomationService
{
    // Constantes de aplicação
    private const string SessionFile = "discord_session.json";
    private const string CommandPrefix = "!play ";
    // Canal do navegador instalado no sistema (Chrome/Edge) — tem os codecs
    // proprietários (H.264/AAC) e o Widevine que o YouTube exige. O Chromium
    // empacotado pelo Playwright NÃO os possui e falha ao reproduzir vídeos.
    private const string BrowserChannel = "chrome";

    // Timeouts (ms) — centralizados para facilitar ajuste
    private const int LoginFieldTimeoutMs = 10_000;
    private const int LoginRedirectTimeoutMs = 60_000;
    private const int NavigationTimeoutMs = 60_000;
    private const int ModalTimeoutMs = 5_000;
    private const int ChatLoadTimeoutMs = 45_000;
    private const int PollIntervalMs = 2_000;
    private const int PlaybackPollMs = 3_000;
    private const int VideoReadyTimeoutMs = 15_000;
    private const int VoiceJoinSettleMs = 3_000;
    private const int VoiceButtonTimeoutMs = 5_000;
    private const int ShareButtonProbeMs = 4_000;
    private const int ShareMaxAttempts = 3;
    private const int DeafenMaxProbes = 5;

    // Tempo ocioso na chamada (fila vazia) antes de desconectar
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    // Seletores / rótulos da UI web do Discord (pt-BR) — dependentes de locale
    private const string ContinueInBrowserSelector = "text=Continuar no Navegador";
    private const string JoinVoiceButtonLabel = "Entrar na chamada de voz";
    private const string DisconnectButtonLabel = "Desconectar";
    private const string ShareScreenLabel = "Compartilhar sua tela";
    private const string ChatMessageSelector = "div[data-list-item-id^='chat-messages']";
    private const string MessageContentSelector = "div[class*='messageContent']";
    private const string VideoSelector = "video";

    // Hosts permitidos para o comando !play (mitiga navegação a URL arbitrária)
    private static readonly string[] AllowedYouTubeHosts =
    {
        "youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtu.be"
    };

    private readonly ILogger<PlaywrightDiscordService> _logger;

    // Fila de reprodução (produtor: monitor do chat; consumidor: player)
    private readonly Channel<string> _queue =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _discordPage;   // permanece no canal de texto para o monitoramento
    private IPage? _voicePage;     // página dedicada ao canal de voz / compartilhamento
    private IPage? _youtubePage;   // página reutilizada do YouTube

    public PlaywrightDiscordService(ILogger<PlaywrightDiscordService> logger)
    {
        _logger = logger;
    }

    // Guards que forçam a ordem de chamada (falha clara em vez de NullReferenceException)
    private IBrowserContext Context =>
        _context ?? throw new InvalidOperationException("StartBrowserAsync deve ser chamado antes desta operação.");

    private IPage DiscordPage =>
        _discordPage ?? throw new InvalidOperationException("LoginToDiscordAsync deve ser chamado antes desta operação.");

    public async Task StartBrowserAsync(CancellationToken cancellationToken = default)
    {
        using var activity = DiagnosticsConfig.ActivitySource.StartActivity("StartBrowser");
        cancellationToken.ThrowIfCancellationRequested();

        _playwright = await Playwright.CreateAsync();

        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Channel = BrowserChannel,
            Headless = false,
            Args = new[]
            {
                "--auto-select-desktop-capture-source=YouTube",
                "--autoplay-policy=no-user-gesture-required",
                "--disable-accelerated-video-encode",
                // Cria microfone/câmera falsos: o Discord exige um dispositivo de
                // áudio para conectar na voz; sem isso dá erro 3002 (mic/áudio).
                "--use-fake-device-for-media-stream",
                // Impede o Chrome de estrangular a aba do YouTube quando ela fica
                // em segundo plano (a de voz vem à frente para compartilhar), o que
                // causava o "Algo deu errado" e travava a reprodução/áudio.
                "--disable-background-timer-throttling",
                "--disable-backgrounding-occluded-windows",
                "--disable-renderer-backgrounding"
            }
        });

        var contextOptions = new BrowserNewContextOptions
        {
            Permissions = new[] { "microphone", "camera" }
        };

        var hasSession = File.Exists(SessionFile);
        activity?.SetTag("session.restored", hasSession);
        if (hasSession)
        {
            contextOptions.StorageStatePath = SessionFile;
            _logger.LogInformation("Sessão salva detectada. Tentando carregar...");
        }

        _context = await _browser.NewContextAsync(contextOptions);
        _logger.LogInformation("Browser iniciado.");
    }

    public async Task LoginToDiscordAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        using var activity = DiagnosticsConfig.ActivitySource.StartActivity("LoginToDiscord");
        cancellationToken.ThrowIfCancellationRequested();

        _discordPage = await Context.NewPageAsync();
        _logger.LogInformation("Acessando tela de login...");
        await _discordPage.GotoAsync("https://discord.com/login");

        var emailInput = _discordPage.Locator("input[name='email']");
        if (await emailInput.IsVisibleAsync(new() { Timeout = LoginFieldTimeoutMs }))
        {
            await emailInput.FillAsync(email);
            await _discordPage.FillAsync("input[name='password']", password);
            await _discordPage.ClickAsync("button[type='submit']");

            _logger.LogInformation("Aguardando autenticação...");
            await _discordPage.WaitForURLAsync("**/channels/**", new PageWaitForURLOptions { Timeout = LoginRedirectTimeoutMs });
            await Context.StorageStateAsync(new BrowserContextStorageStateOptions { Path = SessionFile });
            activity?.SetTag("login.performed", true);
            _logger.LogInformation("Login realizado e sessão salva com sucesso.");
        }
        else
        {
            activity?.SetTag("login.performed", false);
            _logger.LogInformation("Tela de login não exibida; sessão existente reutilizada.");
        }
    }

    public async Task ListenForCommandsAndStreamAsync(string serverId, string textChannelId, string voiceChannelId, CancellationToken cancellationToken = default)
    {
        using var activity = DiagnosticsConfig.ActivitySource.StartActivity("ListenForCommands");
        activity?.SetTag("discord.server_id", serverId);
        activity?.SetTag("discord.text_channel_id", textChannelId);

        var targetUrl = $"https://discord.com/channels/{serverId}/{textChannelId}";
        _logger.LogInformation("Navegando para: {TargetUrl}", targetUrl);

        await DiscordPage.GotoAsync(targetUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = NavigationTimeoutMs });

        var continueLink = DiscordPage.Locator(ContinueInBrowserSelector);
        if (await continueLink.IsVisibleAsync(new() { Timeout = ModalTimeoutMs }))
        {
            _logger.LogInformation("Modal detectada, clicando...");
            await continueLink.ClickAsync();
            await DiscordPage.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }

        _logger.LogInformation("Aguardando carregamento do chat...");
        try
        {
            await DiscordPage.WaitForSelectorAsync(ChatMessageSelector, new() { Timeout = ChatLoadTimeoutMs });
        }
        catch (TimeoutException ex)
        {
            await DiscordPage.ScreenshotAsync(new() { Path = "debug_error.png" });
            _logger.LogError(ex, "Chat não carregou. Screenshot salva como 'debug_error.png'.");
            throw;
        }

        _logger.LogInformation("Sucesso! Chat carregado. Monitorando...");

        // Baseline: só processa comandos POSTERIORES ao boot. Sem isso, um !play
        // antigo já presente no chat faria o bot entrar sozinho na chamada.
        var baselineMessageId = await GetCurrentLastMessageIdAsync();

        // Se um dos loops encerrar (erro/fim), cancela o outro para não travar o WhenAll.
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = runCts.Token;

        async Task GuardAsync(Func<Task> work, string name)
        {
            try
            {
                await work();
            }
            catch (OperationCanceledException)
            {
                // encerramento normal
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Loop '{Loop}' encerrado por erro.", name);
            }
            finally
            {
                runCts.Cancel();
            }
        }

        await Task.WhenAll(
            GuardAsync(() => MonitorChatAsync(baselineMessageId, token), "monitor"),
            GuardAsync(() => ProcessQueueAsync(serverId, voiceChannelId, token), "player"));
    }

    // Produtor: lê o chat e enfileira comandos !play válidos.
    private async Task MonitorChatAsync(string lastMessageId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var messageLocators = DiscordPage.Locator(ChatMessageSelector);
            int count = await messageLocators.CountAsync();

            if (count > 0)
            {
                var lastMessage = messageLocators.Nth(count - 1);
                var messageId = await lastMessage.GetAttributeAsync("data-list-item-id");

                if (messageId != null && messageId != lastMessageId)
                {
                    lastMessageId = messageId;
                    var textContent = await lastMessage.Locator(MessageContentSelector).InnerTextAsync();

                    if (textContent.StartsWith(CommandPrefix))
                    {
                        var youtubeUrl = textContent[CommandPrefix.Length..].Trim();

                        if (!IsAllowedYouTubeUrl(youtubeUrl))
                        {
                            _logger.LogWarning("Comando ignorado — URL não permitida (apenas YouTube): {YoutubeUrl}", youtubeUrl);
                        }
                        else
                        {
                            _queue.Writer.TryWrite(youtubeUrl);
                            _logger.LogInformation("Comando recebido e adicionado à fila: {YoutubeUrl}", youtubeUrl);
                        }
                    }
                }
            }

            await Task.Delay(PollIntervalMs, cancellationToken);
        }
    }

    // Consumidor: toca a fila em sequência; ao esvaziar, aguarda IdleTimeout e sai da call.
    private async Task ProcessQueueAsync(string serverId, string voiceChannelId, CancellationToken cancellationToken)
    {
        bool inCall = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            var youtubeUrl = await DequeueWithIdleTimeoutAsync(inCall, cancellationToken);

            if (youtubeUrl is null)
            {
                // Fila vazia por IdleTimeout enquanto na chamada → desconecta.
                if (inCall)
                {
                    _logger.LogInformation("Fila vazia por {Minutos} min. Saindo do canal de voz.", IdleTimeout.TotalMinutes);
                    await LeaveVoiceAsync();
                    inCall = false;
                }
                continue;
            }

            if (!inCall)
            {
                await JoinVoiceAndShareAsync(serverId, voiceChannelId, youtubeUrl, cancellationToken);
                inCall = true;
            }
            else
            {
                _logger.LogInformation("Próximo da fila. Trocando vídeo: {YoutubeUrl}", youtubeUrl);
                await PlayVideoAsync(youtubeUrl, cancellationToken);
            }

            await WaitForVideoEndAsync(cancellationToken);
            _logger.LogInformation("Reprodução finalizada.");
        }
    }

    // Retira o próximo item. Se na chamada e a fila estiver vazia, aguarda no
    // máximo IdleTimeout e retorna null (sinal para sair). Fora da chamada,
    // aguarda indefinidamente pelo próximo comando.
    private async Task<string?> DequeueWithIdleTimeoutAsync(bool applyIdleTimeout, CancellationToken cancellationToken)
    {
        if (_queue.Reader.TryRead(out var url))
            return url;

        if (!applyIdleTimeout)
        {
            while (await _queue.Reader.WaitToReadAsync(cancellationToken))
                if (_queue.Reader.TryRead(out url))
                    return url;
            return null;
        }

        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idleCts.CancelAfter(IdleTimeout);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(idleCts.Token))
                if (_queue.Reader.TryRead(out url))
                    return url;
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null; // estourou o IdleTimeout (não o cancelamento global)
        }
    }

    private async Task JoinVoiceAndShareAsync(string serverId, string voiceChannelId, string youtubeUrl, CancellationToken cancellationToken)
    {
        using var activity = DiagnosticsConfig.ActivitySource.StartActivity("JoinVoiceAndShare");
        activity?.SetTag("youtube.url", youtubeUrl);

        // Página dedicada ao canal de voz: NÃO reaproveita _discordPage,
        // para que o loop de monitoramento continue no canal de texto.
        _voicePage ??= await Context.NewPageAsync();
        var voiceUrl = $"https://discord.com/channels/{serverId}/{voiceChannelId}";

        // Abre o vídeo primeiro (aba dedicada); o compartilhamento captura essa aba.
        await PlayVideoAsync(youtubeUrl, cancellationToken);

        // O erro 3002 do Discord (falha de conexão RTC) impede os controles de
        // transmissão de aparecerem. Recolocar-se na chamada (nova navegação +
        // entrar) refaz a conexão — tentamos algumas vezes antes de desistir.
        for (int attempt = 1; attempt <= ShareMaxAttempts && !cancellationToken.IsCancellationRequested; attempt++)
        {
            await NavigateAndJoinVoiceAsync(voiceUrl, cancellationToken);

            if (await TryStartScreenShareAsync(cancellationToken))
            {
                _logger.LogInformation("Transmissão iniciada.");
                // Traz a aba do YouTube de volta ao primeiro plano: em segundo
                // plano o Chrome estrangula a página e o vídeo mostra "Algo deu
                // errado". A captura de tela continua mesmo com ela em foco.
                if (_youtubePage is not null)
                    await _youtubePage.BringToFrontAsync();
                return;
            }

            _logger.LogWarning(
                "Não foi possível iniciar a transmissão (tentativa {Attempt}/{Max}); possível erro 3002. Refazendo a conexão de voz...",
                attempt, ShareMaxAttempts);
        }

        await _voicePage.ScreenshotAsync(new() { Path = "erro_final_share.png" });
        await LogVoicePageStateAsync();
        activity?.SetStatus(ActivityStatusCode.Error, "Não foi possível iniciar a transmissão (erro 3002?).");
        _logger.LogError(
            "Falha ao compartilhar a tela após {Max} tentativas. Verifique a conexão/VPN e o erro 3002 do Discord.",
            ShareMaxAttempts);
    }

    private async Task NavigateAndJoinVoiceAsync(string voiceUrl, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Entrando no canal de voz...");
        await _voicePage!.GotoAsync(voiceUrl);
        await Task.Delay(VoiceJoinSettleMs, cancellationToken);

        var joinVoiceButton = _voicePage.GetByRole(AriaRole.Button, new() { Name = JoinVoiceButtonLabel });
        if (await joinVoiceButton.IsVisibleAsync(new() { Timeout = VoiceButtonTimeoutMs }))
        {
            await joinVoiceButton.ClickAsync();
            _logger.LogInformation("Entrou no canal de voz.");
            await Task.Delay(PollIntervalMs, cancellationToken);
        }
        else
        {
            _logger.LogInformation("Botão de entrar não encontrado (pode já estar conectado).");
        }

        // Entra mudo: ativar o "surdo" também silencia o microfone.
        await EnsureSelfDeafenedAsync();
    }

    // Garante que o bot esteja no modo "surdo" (mic mudo + sem áudio). Só clica se
    // o botão indicar o estado "ativar" — evita desativar caso já esteja surdo
    // (o estado persiste na sessão salva).
    private async Task EnsureSelfDeafenedAsync()
    {
        if (_voicePage is null)
            return;

        // Clica no botão "surdo" apenas quando ele está no estado "ativar"
        // (rótulo pt-BR começa com "Ativar..."; en: "Deafen"). Assim não desativa
        // caso a sessão já esteja surda. Surdo também silencia o microfone.
        const string script = @"() => {
            const norm = s => (s || '').toLowerCase();
            const btns = Array.from(document.querySelectorAll('button[aria-label]'));
            const b = btns.find(x => {
                const n = norm(x.getAttribute('aria-label'));
                return n.includes('surdo') || n.includes('ensurdec') || n.includes('deafen');
            });
            if (!b) return 'notfound';
            const label = b.getAttribute('aria-label');
            const n = norm(label);
            const activate = n.startsWith('ativar') || n.startsWith('deafen');
            if (activate) { b.click(); return 'clicked:' + label; }
            return 'already:' + label;
        }";

        // O painel de voz pode demorar a renderizar após entrar na call.
        for (int i = 0; i < DeafenMaxProbes; i++)
        {
            try
            {
                var result = await _voicePage.EvaluateAsync<string>(script);
                if (result != "notfound")
                {
                    _logger.LogInformation("Auto-surdo (mic + áudio mudos): {Result}", result);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Falha ao tentar ativar o modo surdo.");
            }

            await Task.Delay(VoiceJoinSettleMs);
        }

        _logger.LogWarning("Não foi possível ativar o modo surdo: botão do painel de voz não encontrado.");
    }

    private async Task<bool> TryStartScreenShareAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _voicePage!.BringToFrontAsync();

        // A UI do Discord (pt-BR) varia; tentamos alguns rótulos/roles do botão de tela.
        ILocator[] candidates =
        {
            _voicePage.GetByLabel(ShareScreenLabel),
            _voicePage.GetByLabel("Compartilhar tela"),
            _voicePage.GetByRole(AriaRole.Button, new() { Name = ShareScreenLabel }),
            _voicePage.GetByRole(AriaRole.Button, new() { Name = "Compartilhar tela" }),
            _voicePage.GetByRole(AriaRole.Button, new() { Name = "Tela" }),
        };

        foreach (var candidate in candidates)
        {
            try
            {
                var target = candidate.First;
                await target.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = ShareButtonProbeMs });
                await target.ClickAsync();
                return true;
            }
            catch (TimeoutException)
            {
                // tenta o próximo candidato
            }
        }

        return false;
    }

    // Diagnóstico: quando o compartilhamento falha, registra os botões visíveis na
    // página de voz e se o banner de erro 3002 está presente — para identificar se
    // é rótulo divergente ou falha de conexão RTC do Discord.
    private async Task LogVoicePageStateAsync()
    {
        if (_voicePage is null)
            return;

        try
        {
            var has3002 = await _voicePage.EvaluateAsync<bool>(
                "() => (document.body?.innerText ?? '').includes('3002')");
            var labels = await _voicePage.EvaluateAsync<string[]>(
                "() => Array.from(document.querySelectorAll('button,[role=button]'))" +
                ".map(b => (b.getAttribute('aria-label') || b.textContent || '').trim())" +
                ".filter(t => t).slice(0, 60)");

            _logger.LogWarning(
                "Diagnóstico da página de voz — banner 3002 presente: {Has3002}; botões: {Buttons}",
                has3002, string.Join(" | ", labels));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Não foi possível coletar o diagnóstico da página de voz.");
        }
    }

    private async Task PlayVideoAsync(string youtubeUrl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _youtubePage ??= await Context.NewPageAsync();

        _logger.LogInformation("Reproduzindo: {YoutubeUrl}", youtubeUrl);
        await _youtubePage.GotoAsync(youtubeUrl);

        try
        {
            await _youtubePage.WaitForSelectorAsync(VideoSelector, new() { Timeout = VideoReadyTimeoutMs });
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Player do YouTube não carregou a tempo para: {YoutubeUrl}", youtubeUrl);
            return;
        }

        // Inicia a reprodução de forma idempotente (não pausa vídeo que já toca).
        try
        {
            await _youtubePage.EvaluateAsync(
                "async () => { const v = document.querySelector('video'); if (v) { try { await v.play(); } catch (e) {} } }");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Não foi possível iniciar a reprodução via script (opcional).");
        }
    }

    // Aguarda o vídeo atual terminar (ou a página deixar de ter <video>).
    private async Task WaitForVideoEndAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            bool ended;
            try
            {
                ended = await _youtubePage!.EvaluateAsync<bool>(
                    "() => { const v = document.querySelector('video'); if (!v) return true; " +
                    "return v.ended || (v.duration > 0 && v.currentTime >= v.duration - 1.5); }");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Falha ao ler o estado do vídeo; considerando finalizado.");
                return;
            }

            if (ended)
                return;

            await Task.Delay(PlaybackPollMs, cancellationToken);
        }
    }

    private async Task LeaveVoiceAsync()
    {
        if (_voicePage is null)
            return;

        try
        {
            var disconnect = _voicePage.GetByRole(AriaRole.Button, new() { Name = DisconnectButtonLabel });
            if (await disconnect.IsVisibleAsync(new() { Timeout = VoiceButtonTimeoutMs }))
            {
                await disconnect.ClickAsync();
                _logger.LogInformation("Desconectado do canal de voz.");
            }
            else
            {
                _logger.LogInformation("Botão de desconectar não encontrado (pode já estar fora da chamada).");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao sair do canal de voz.");
        }
    }

    private async Task<string> GetCurrentLastMessageIdAsync()
    {
        var messageLocators = DiscordPage.Locator(ChatMessageSelector);
        int count = await messageLocators.CountAsync();
        if (count == 0)
            return "";

        return await messageLocators.Nth(count - 1).GetAttributeAsync("data-list-item-id") ?? "";
    }

    private static bool IsAllowedYouTubeUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && AllowedYouTubeHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_browser is not null)
            {
                // Fechar o browser encerra contexto e páginas associadas.
                await _browser.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha ao fechar o browser durante o descarte.");
        }
        finally
        {
            _playwright?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
