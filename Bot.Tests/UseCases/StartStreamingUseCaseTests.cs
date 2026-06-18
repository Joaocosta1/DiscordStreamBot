namespace Bot.Tests.UseCases;

using Xunit;
using Moq;
using Bot.Application.UseCases;
using Bot.Application.Interfaces;
using Bot.Domain.Entities;

public class StartStreamingUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_DeveChamarServicosDoBrowserNaOrdemCorreta()
    {
        // Arrange
        var browserMock = new Mock<IBrowserAutomationService>();
        var useCase = new StartStreamingUseCase(browserMock.Object);
        var session = new StreamSession("server123", "text123", "voice123");

        // Act
        await useCase.ExecuteAsync("teste@email.com", "senha123", session);

        // Assert
        browserMock.Verify(b => b.StartBrowserAsync(), Times.Once);
        browserMock.Verify(b => b.LoginToDiscordAsync("teste@email.com", "senha123"), Times.Once);
        browserMock.Verify(b => b.ListenForCommandsAndStreamAsync("server123", "text123", "voice123"), Times.Once);
    }
}