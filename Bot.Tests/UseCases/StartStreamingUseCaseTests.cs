namespace Bot.Tests.UseCases;

using Bot.Application.Interfaces;
using Bot.Application.UseCases;
using Bot.Domain.Entities;
using Moq;
using Xunit;

public class StartStreamingUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_DeveChamarServicosDoBrowserNaOrdemCorreta()
    {
        // Arrange
        var browserMock = new Mock<IBrowserAutomationService>();
        var useCase = new StartStreamingUseCase(browserMock.Object);
        var session = new StreamSession("server123", "text123", "voice123");

        var callOrder = new List<string>();
        browserMock.Setup(b => b.StartBrowserAsync(It.IsAny<CancellationToken>()))
                   .Callback(() => callOrder.Add(nameof(IBrowserAutomationService.StartBrowserAsync)))
                   .Returns(Task.CompletedTask);
        browserMock.Setup(b => b.LoginToDiscordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .Callback(() => callOrder.Add(nameof(IBrowserAutomationService.LoginToDiscordAsync)))
                   .Returns(Task.CompletedTask);
        browserMock.Setup(b => b.ListenForCommandsAndStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .Callback(() => callOrder.Add(nameof(IBrowserAutomationService.ListenForCommandsAndStreamAsync)))
                   .Returns(Task.CompletedTask);

        // Act
        await useCase.ExecuteAsync("teste@email.com", "senha123", session);

        // Assert - cada serviço foi chamado uma vez com os argumentos corretos
        browserMock.Verify(b => b.StartBrowserAsync(It.IsAny<CancellationToken>()), Times.Once);
        browserMock.Verify(b => b.LoginToDiscordAsync("teste@email.com", "senha123", It.IsAny<CancellationToken>()), Times.Once);
        browserMock.Verify(b => b.ListenForCommandsAndStreamAsync("server123", "text123", "voice123", It.IsAny<CancellationToken>()), Times.Once);
        browserMock.VerifyNoOtherCalls();

        // Assert - as chamadas ocorreram na ordem correta
        Assert.Equal(
            new[]
            {
                nameof(IBrowserAutomationService.StartBrowserAsync),
                nameof(IBrowserAutomationService.LoginToDiscordAsync),
                nameof(IBrowserAutomationService.ListenForCommandsAndStreamAsync)
            },
            callOrder);
    }
}
