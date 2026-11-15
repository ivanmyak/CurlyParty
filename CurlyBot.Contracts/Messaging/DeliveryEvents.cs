namespace CurlyBot.Contracts.Messaging;

/// <summary>
///     Доставка уже синтезированной речи пользователю другой платформы.
///     Используется, когда /talk с is-in-current-service-only=false должен
///     дополнительно прийти связанному аккаунту в Telegram или Discord.
/// </summary>
public sealed record SpeechDeliveryRequested(
    Guid DeliveryId,
    string TargetPlatform,
    string TargetExternalId,
    string TargetDisplayName,
    string? SourceDisplayName,
    string? NotificationText,
    string WavPath,
    string? OggPath);
