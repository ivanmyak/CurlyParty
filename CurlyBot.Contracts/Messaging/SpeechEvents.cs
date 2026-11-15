namespace CurlyBot.Contracts.Messaging;

/// <summary>Запрос на синтез речи. ReplyTo — routing key очереди, куда вернуть результат.</summary>
public sealed record SpeechRequested(
    Guid RequestId,
    string Text,
    string ReplyTo,
    DateTimeOffset RequestedAt);

/// <summary>
///     Единый ответ TTS-сервиса. Success=false означает, что синтез не удался,
///     а Error содержит причину. Один тип ответа позволяет держать одну reply-очередь.
/// </summary>
public sealed record SpeechCompleted(
    Guid RequestId,
    bool Success,
    string? WavPath,
    string? OggPath,
    long SizeBytes,
    string? Error,
    DateTimeOffset CompletedAt);
