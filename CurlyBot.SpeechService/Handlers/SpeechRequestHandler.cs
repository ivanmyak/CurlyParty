using CurlyBot.Contracts.Messaging;
using CurlyBot.Messaging;
using CurlyBot.SpeechService.Services;

namespace CurlyBot.SpeechService.Handlers;

/// <summary>Обрабатывает запросы на синтез речи и публикует результат в очередь отправителя.</summary>
public sealed class SpeechRequestHandler(
    SpeechSynthesisService synthesis,
    IIntegrationEventPublisher publisher,
    ILogger<SpeechRequestHandler> logger)
    : IIntegrationEventHandler<SpeechRequested>
{
    /// <summary>Синтезирует речь по событию и отправляет результат или описание ошибки.</summary>
    /// <param name="integrationEvent">Запрос на синтез речи.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача обработки.</returns>
    public async Task HandleAsync(SpeechRequested integrationEvent, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(integrationEvent.ReplyTo))
        {
            logger.LogWarning("TTS-запрос {RequestId} без ReplyTo — результат некому вернуть", integrationEvent.RequestId);
            return;
        }

        try
        {
            logger.LogInformation(
                "TTS-запрос {RequestId}: {Length} символов, reply={ReplyTo}",
                integrationEvent.RequestId,
                integrationEvent.Text.Length,
                integrationEvent.ReplyTo);

            var result = await synthesis.SynthesizeAsync(integrationEvent.Text, cancellationToken);

            await publisher.PublishAsync(
                integrationEvent.ReplyTo,
                new SpeechCompleted(
                    integrationEvent.RequestId,
                    true,
                    result.WavPath,
                    result.OggPath,
                    result.SizeBytes,
                    null,
                    DateTimeOffset.UtcNow),
                cancellationToken);

            logger.LogInformation("TTS-запрос {RequestId} выполнен: {WavPath}", integrationEvent.RequestId, result.WavPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TTS-запрос {RequestId} завершился ошибкой", integrationEvent.RequestId);

            await publisher.PublishAsync(
                integrationEvent.ReplyTo,
                new SpeechCompleted(
                    integrationEvent.RequestId,
                    false,
                    null,
                    null,
                    0,
                    ex.Message,
                    DateTimeOffset.UtcNow),
                cancellationToken);
        }
    }
}
