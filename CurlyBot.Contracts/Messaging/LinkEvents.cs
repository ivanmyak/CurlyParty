namespace CurlyBot.Contracts.Messaging;

/// <summary>
///     Запрос подтверждения связки аккаунтов. Отправляется боту целевой платформы,
///     который должен показать пользователю кнопки «Подтвердить» / «Отклонить».
/// </summary>
public sealed record LinkConfirmationRequested(
    Guid RequestId,
    string TargetPlatform,
    string TargetExternalId,
    string TargetDisplayName,
    string SourcePlatform,
    string SourceExternalId,
    string SourceDisplayName,
    string? SourceUsername,
    DateTimeOffset ExpiresAt);

/// <summary>Результат связки, который отправляется боту инициатора.</summary>
public sealed record LinkResultNotification(
    Guid RequestId,
    string SourcePlatform,
    string SourceExternalId,
    string SourceDisplayName,
    bool Accepted,
    string? Reason);
