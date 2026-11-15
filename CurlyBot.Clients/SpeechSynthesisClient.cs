using CurlyBot.Contracts.Messaging;
using CurlyBot.Messaging;

namespace CurlyBot.Clients;

/// <summary>Результат синтеза речи.</summary>
/// <param name="WavPath">Путь к WAV-файлу на общем volume.</param>
/// <param name="OggPath">Путь к OGG-файлу для голосовых сообщений, если он создан.</param>
/// <param name="SizeBytes">Размер полученного аудио в байтах.</param>
public sealed record SpeechSynthesisResult(string WavPath, string? OggPath, long SizeBytes);

/// <summary>
///     Клиент TTS-сервиса через RabbitMQ.
///     Публикует SpeechRequested и ждёт SpeechCompleted в собственной reply-очереди.
///     Один экземпляр — singleton, потому что он же является обработчиком ответов.
/// </summary>
public sealed class SpeechSynthesisClient : IIntegrationEventHandler<SpeechCompleted>
{
    private readonly IIntegrationEventPublisher _publisher;
    private readonly SpeechClientOptions _options;
    private readonly ILogger<SpeechSynthesisClient> _logger;
    private readonly Dictionary<Guid, TaskCompletionSource<SpeechSynthesisResult>> _pending = new();
    private readonly object _sync = new();

    /// <summary>Создаёт клиент TTS на основе опубликованного события и очереди ответов.</summary>
    /// <param name="publisher">Публикатор интеграционных событий.</param>
    /// <param name="options">Настройки клиента, включая очередь ответов.</param>
    /// <param name="logger">Журнал клиента.</param>
    /// <exception cref="InvalidOperationException">Очередь ответов не настроена.</exception>
    public SpeechSynthesisClient(
        IIntegrationEventPublisher publisher,
        IOptions<SpeechClientOptions> options,
        ILogger<SpeechSynthesisClient> logger)
    {
        _publisher = publisher;
        _options = options.Value;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_options.ReplyQueue))
            throw new InvalidOperationException("SpeechClient:ReplyQueue не настроен.");
    }

    /// <summary>Отправляет текст на синтез и ожидает готовое аудио.</summary>
    /// <param name="text">Текст для озвучивания.</param>
    /// <param name="cancellationToken">Токен отмены ожидания.</param>
    /// <returns>Результат синтеза речи.</returns>
    /// <exception cref="TimeoutException">TTS-сервис не ответил за отведённое время.</exception>
    public async Task<SpeechSynthesisResult> SynthesizeAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid();
        var completion = new TaskCompletionSource<SpeechSynthesisResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_sync)
            _pending[requestId] = completion;

        try
        {
            await _publisher.PublishAsync(
                RabbitTopology.SpeechRequestRoutingKey,
                new SpeechRequested(requestId, text, _options.ReplyQueue, DateTimeOffset.UtcNow),
                cancellationToken);

            _logger.LogInformation(
                "Отправлен запрос TTS {RequestId} в очередь {ReplyQueue}",
                requestId,
                _options.ReplyQueue);

            return await completion.Task.WaitAsync(
                TimeSpan.FromSeconds(_options.TimeoutSeconds),
                cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"TTS-сервис не ответил за {_options.TimeoutSeconds} сек. (requestId={requestId})");
        }
        finally
        {
            lock (_sync)
                _pending.Remove(requestId);
        }
    }

    /// <summary>Обрабатывает ответ TTS-сервиса и завершает ожидающий запрос.</summary>
    /// <param name="integrationEvent">Событие с результатом синтеза.</param>
    /// <param name="cancellationToken">Токен отмены обработки.</param>
    /// <returns>Задача обработки события.</returns>
    public Task HandleAsync(SpeechCompleted integrationEvent, CancellationToken cancellationToken)
    {
        TaskCompletionSource<SpeechSynthesisResult>? completion;
        lock (_sync)
        {
            if (!_pending.TryGetValue(integrationEvent.RequestId, out completion))
            {
                _logger.LogDebug(
                    "Получен ответ TTS {RequestId}, но ожидающего запроса нет (таймаут?)",
                    integrationEvent.RequestId);
                return Task.CompletedTask;
            }
        }

        if (!integrationEvent.Success ||
            string.IsNullOrWhiteSpace(integrationEvent.WavPath))
        {
            completion.TrySetException(new SpeechSynthesisException(
                integrationEvent.Error ?? "TTS-сервис вернул ошибку без описания."));
            return Task.CompletedTask;
        }

        completion.TrySetResult(new SpeechSynthesisResult(
            integrationEvent.WavPath,
            integrationEvent.OggPath,
            integrationEvent.SizeBytes));

        return Task.CompletedTask;
    }
}

/// <summary>Исключение, возникающее при ошибке синтеза речи.</summary>
public sealed class SpeechSynthesisException(string message) : Exception(message);
