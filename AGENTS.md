# Repository Guidelines

## Project Overview

DiscordStreamBot is a .NET 10 / C# console bot that **automates the Discord web client via a real browser** (Microsoft.Playwright) — it is *not* a Discord API bot (no Discord.Net / DSharpPlus). It logs into Discord in headed Chromium, watches a text channel for `!play <youtube-url>` commands, joins a voice channel, and screen-shares the YouTube video. Source comments and console/log strings are in Portuguese (pt-BR).

## Architecture & Data Flow

Clean-architecture, 4 layers + tests, dependencies point inward:

```
Bot.Console (composition root, Exe)
   └─ Bot.Infrastructure ─┐
   └─ Bot.Application ─────┤
   └─ Bot.Domain ◄─────────┘
Bot.Tests → Bot.Application, Bot.Domain (never Infrastructure)
```

- **Bot.Domain** — pure entities, zero dependencies.
- **Bot.Application** — interfaces + use cases; references Domain only.
- **Bot.Infrastructure** — Playwright implementation; references Application + Domain.
- **Bot.Console** — the *only* project that references Infrastructure, enforcing dependency direction. Wires DI and is the entry point.

**Data flow:** `Program.cs` builds the generic host, registers services + OpenTelemetry, and calls `host.RunAsync()`. `StreamingWorker` (a `BackgroundService`) loads/validates config, builds a `StreamSession`, and runs `StartStreamingUseCase.ExecuteAsync(email, password, session, stoppingToken)`, which sequentially awaits the injected `IBrowserAutomationService`: `StartBrowserAsync` → `LoginToDiscordAsync` → `ListenForCommandsAndStreamAsync` (all take a `CancellationToken`). `ListenForCommandsAndStreamAsync` runs **two concurrent loops** over a `Channel<string>` play queue: a **monitor** polls the last chat message every 2s and enqueues `!play <url>` commands (validated against a YouTube host allowlist), and a **player** dequeues and streams videos one at a time. A **baseline message id** captured at startup means pre-existing `!play` messages are ignored (the bot no longer auto-joins on boot). The player joins voice on a **dedicated voice page** (`_voicePage`) — the monitoring page stays on the text channel — shares the screen once, then plays each YouTube URL on `_youtubePage`, advancing to the next queued video when the current one ends (`video.ended`). When the queue drains it waits `IdleTimeout` (5 min) for a new command and, if none arrives, **leaves the voice channel** (clicking "Desconectar"); a later command rejoins. Login state persists to `discord_session.json` via Playwright `StorageState`. On Ctrl+C the token cancels, the worker calls `StopApplication`, and the host disposes the service (`IAsyncDisposable` → browser closed).

**Observability:** all runtime output goes through `ILogger` (structured logging), exported via OpenTelemetry — logs, traces (`ActivitySource` spans per browser operation), and runtime metrics — all to the console exporter. No `Console.WriteLine` except the interactive credential prompts in `Program.cs`.

## Key Directories

- `Bot.Domain/Entities/` — e.g. `StreamSession.cs` (validated immutable `record` of `ServerId`, `TextChannelId`, `VoiceChannelId`).
- `Bot.Application/Interfaces/` — e.g. `IBrowserAutomationService.cs`.
- `Bot.Application/UseCases/` — e.g. `StartStreamingUseCase.cs` (sole orchestrator).
- `Bot.Infrastructure/Services/` — e.g. `PlaywrightDiscordService.cs` (only infra impl).
- `Bot.Infrastructure/Diagnostics/` — `DiagnosticsConfig.cs` (shared `ActivitySource` + service name for OpenTelemetry).
- `Bot.Console/` — `Program.cs` (composition root + host) and `StreamingWorker.cs` (`BackgroundService` running the use case).
- `Bot.Tests/UseCases/` — xUnit tests mirroring the Application layout.

Ignore `bin/` and `obj/` — build output only (Playwright's bundled `*.ps1`/`*.sh` installer scripts live there and are not source).

## Development Commands

Standard .NET CLI against the `.slnx` solution:

```bash
dotnet restore
dotnet build DiscordStreamBot.slnx
dotnet run --project Bot.Console
dotnet test                                          # whole solution
dotnet test Bot.Tests/Bot.Tests.csproj               # single project
dotnet test --filter FullyQualifiedName~StartStreamingUseCaseTests
```

A minimal `.editorconfig` defines formatting (UTF-8, 4-space C#, final newline); run `dotnet format` to apply. The bot launches the **system-installed Google Chrome** via Playwright's `Channel = "chrome"` (constant `BrowserChannel` in `PlaywrightDiscordService`) — required because Playwright's bundled Chromium lacks the proprietary codecs (H.264/AAC) and Widevine that YouTube needs. Google Chrome (or Edge, `Channel = "msedge"`) must be installed; the `pwsh bin/.../playwright.ps1 install` step (generated after build) still provides Playwright's driver but the bundled Chromium is not used for playback. Running the bot prints OpenTelemetry logs/traces/metrics to the console via the console exporter.

Screen sharing depends on Discord's browser Go Live. To avoid Discord **RTC error 3002** (which prevents the share controls from rendering), the browser is launched with `--disable-accelerated-video-encode` (software encoding, the analog of disabling H.264 hardware acceleration) and `JoinVoiceAndShareAsync` **retries** the join+share up to `ShareMaxAttempts` (3), re-navigating to the voice channel each time to re-establish the connection. Note `--mute-audio` mutes browser audio output (shared audio is silent) — remove it if you need sound in the stream.

## Code Conventions & Common Patterns

- **Runtime config:** all projects `net10.0`, `ImplicitUsings=enable`, `Nullable=enable`. File-scoped namespaces; `using` directives placed after the namespace declaration.
- **Naming:** PascalCase types/methods; interfaces `I`-prefixed; async methods carry the `Async` suffix and return `Task`. Test methods use Portuguese `Method_DeveExpectedBehavior` (e.g. `ExecuteAsync_DeveChamarServicosDoBrowserNaOrdemCorreta`).
- **Async:** everything is `async`/`await` Task-based; use cases `await` service calls in sequence.
- **Dependency injection & host:** Microsoft.Extensions.Hosting generic host, run via `await builder.Build().RunAsync()`. Constructor injection only. Registration + telemetry wiring in `Program.cs`:
  ```csharp
  services.AddSingleton<IBrowserAutomationService, PlaywrightDiscordService>();
  services.AddTransient<StartStreamingUseCase>();
  services.AddHostedService<StreamingWorker>();
  services.AddOpenTelemetry()
      .ConfigureResource(r => r.AddService(DiagnosticsConfig.ServiceName))
      .WithTracing(t => t.AddSource(DiagnosticsConfig.ServiceName).AddConsoleExporter())
      .WithMetrics(m => m.AddRuntimeInstrumentation().AddConsoleExporter());
  ```
  Logging is wired via `ConfigureLogging` → `ClearProviders()` + `AddOpenTelemetry(...).AddConsoleExporter()`.
- **Cancellation & lifecycle:** long-running work is a `BackgroundService` (`StreamingWorker`); the monitoring loop is `while (!token.IsCancellationRequested)` with `await Task.Delay(interval, token)`. `IBrowserAutomationService : IAsyncDisposable`; the DI container disposes the singleton on shutdown (Chrome closed there).
- **Concurrency (producer/consumer):** the chat monitor and the video player run as two concurrent loops linked by a `Channel<string>` play queue (`SingleReader`/`SingleWriter`). A linked `CancellationTokenSource` (`GuardAsync` wrapper) stops both loops if either faults. The player advances on `video.ended` (polled via `EvaluateAsync<bool>`) and applies a 5-min `IdleTimeout` (`Channel.Reader.WaitToReadAsync` + `CancelAfter`) before leaving voice. Playwright pages are single-purpose (`_discordPage` monitor, `_voicePage` voice/share, `_youtubePage` playback) so the loops never touch the same page.
- **Guard clauses:** the service exposes `Context`/`DiscordPage` properties that throw `InvalidOperationException` if used out of order, instead of dereferencing `null!`.
- **Constants:** selectors, pt-BR UI labels, and timeouts are `private const` at the top of `PlaywrightDiscordService` (locale-dependent labels kept in one place).
- **Logging & observability:** inject `ILogger<T>` and use structured templates (`_logger.LogInformation("Navegando para: {TargetUrl}", url)`), never `Console.WriteLine` (except interactive prompts). Wrap each browser operation in `DiagnosticsConfig.ActivitySource.StartActivity("...")` and tag it (`activity?.SetTag(...)`, `activity?.SetStatus(ActivityStatusCode.Error, ...)` on failure).
- **Error handling:** `StreamingWorker.ExecuteAsync` wraps the run in `try/catch`, logs `LogCritical` on failure, and always calls `StopApplication` in `finally`; config errors surface as an `InvalidOperationException` listing the missing keys. The Playwright service wraps selectors in `try/catch`, logs with `LogError(ex, ...)`, saves screenshots on failure (`debug_error.png`, `erro_final_share.png`), and rethrows on chat-load timeout.
- **Security:** the `!play` command validates the target against a YouTube host allowlist (`youtube.com`, `youtu.be`, …) before navigating; non-YouTube URLs are logged and skipped.
- **State:** domain entities are validated immutable `record`s (`StreamSession`). Browser session state persists to `discord_session.json` (gitignored, along with `config.txt` and screenshots).

## Important Files

- `Bot.Console/Program.cs` — host build, DI + OpenTelemetry wiring, `RunAsync`.
- `Bot.Console/StreamingWorker.cs` — `BackgroundService`; config load/validation + runs the use case.
- `Bot.Infrastructure/Services/PlaywrightDiscordService.cs` — all Discord/browser automation.
- `Bot.Infrastructure/Diagnostics/DiagnosticsConfig.cs` — shared `ActivitySource` + service name for OpenTelemetry.
- `Bot.Application/UseCases/StartStreamingUseCase.cs` — orchestration logic.
- `Bot.Application/Interfaces/IBrowserAutomationService.cs` — the browser contract.
- `DiscordStreamBot.slnx` — XML-format solution (not classic `.sln`) listing all 5 projects.
- `config.txt` — runtime secrets (`KEY=VALUE`); gitignored, required keys validated at startup.

## Runtime/Tooling Preferences

- **Runtime:** .NET 10 SDK (`net10.0`); C# with implicit usings + nullable enabled.
- **Package manager:** NuGet via `dotnet` CLI. Key packages: `Microsoft.Playwright` 1.42.0 (Infrastructure); `Microsoft.Extensions.Hosting` 9.0.0 + `OpenTelemetry.Extensions.Hosting` / `OpenTelemetry.Exporter.Console` / `OpenTelemetry.Instrumentation.Runtime` 1.17.0 (Console); `Microsoft.Extensions.Logging.Abstractions` (Infrastructure, for `ILogger<T>`). `Bot.Infrastructure.csproj` sets `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>` so Playwright assets copy to consumers.
- **Configuration/secrets:** no `appsettings.json`, `IConfiguration`, user-secrets, or env vars. `StreamingWorker` reads a plain-text `config.txt` (`KEY=VALUE`, `Split('=', 2)`) with keys `EMAIL`, `SENHA`, `GUILD_ID`, `TEXT_CHANNEL_ID`, `VOICE_CHANNEL_ID`, validating all are present (clear error otherwise) and falling back to interactive prompts. `config.txt`, `discord_session.json`, and debug screenshots are gitignored — keep credentials out of source control.
- A `.editorconfig` is present (UTF-8, spaces, final newline). No `Directory.Build.props`, `global.json`, or `nuget.config`.

## Testing & QA

- **Framework:** xUnit 2.7.0 (`[Fact]`), runner `xunit.runner.visualstudio` 2.5.7, `Microsoft.NET.Test.Sdk` 17.9.0.
- **Mocking:** Moq 4.20.70. `StartStreamingUseCaseTests` verifies both the calls (`Verify(..., Times.Once)` with `It.IsAny<CancellationToken>()` + `VerifyNoOtherCalls()`) and their order (each invocation recorded via `Callback` into a list, then `Assert.Equal` against the expected sequence). FluentAssertions 6.12.0 is referenced but currently unused.
- **Style:** explicit `// Arrange` / `// Act` / `// Assert` blocks.
- **Coverage:** minimal — one test file (`StartStreamingUseCaseTests.cs`) covering `StartStreamingUseCase` orchestration order. New Application use cases should get parallel tests under `Bot.Tests/UseCases/`, mocking service interfaces. Tests reference only Application + Domain.
- **Run:** `dotnet test`. No `.runsettings`/`xunit.runner.json`; no coverage tooling configured.

_No CI, Dockerfile, or deployment scripts exist. README.md is a stub._
