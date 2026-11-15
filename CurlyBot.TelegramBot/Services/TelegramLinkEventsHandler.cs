using CurlyBot.Clients;
using CurlyBot.Contracts;
using CurlyBot.Contracts.Messaging;
using CurlyBot.Messaging;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types.ReplyMarkups;

namespace CurlyBot.TelegramBot.Services;

/// <summary>
///     Обрабатывает события, адресованные Telegram-боту: запросы подтверждения связки,
///     результаты связки и готовую озвучку от других сервисов.
/// </summary>
public sealed class TelegramLinkEventsHandler(
    IdentityServiceClient identity,
    TelegramSender sender,
    ILogger<TelegramLinkEventsHandler> logger)
    : IIntegrationEventHandler<LinkConfirmationRequested>,
      IIntegrationEventHandler<LinkResultNotification>,
      IIntegrationEventHandler<SpeechDeliveryRequested>
{
    /// <summary>Отправляет получателю запрос на подтверждение связки.</summary>
    /// <param name="integrationEvent">Событие запроса связки.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача обработки.</returns>
    public async Task HandleAsync(
        LinkConfirmationRequested integrationEvent,
        CancellationToken cancellationToken)
    {
        if (Platforms.Normalize(integrationEvent.TargetPlatform) != Platforms.Telegram)
            return;

        if (!long.TryParse(integrationEvent.TargetExternalId, out var chatId))
        {
            await NotifyFailedAsync(integrationEvent.RequestId, "Некорректный Telegram ID", cancellationToken);
            return;
        }

        var markup = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("✅ Подтвердить", $"link:accept:{integrationEvent.RequestId}"),
                InlineKeyboardButton.WithCallbackData("❌ Отклонить", $"link:decline:{integrationEvent.RequestId}")
            }
        });

        var text =
            "Запрос на связку аккаунтов\n\n" +
            $"Пользователь {integrationEvent.SourceDisplayName} " +
            $"({Platforms.DisplayName(integrationEvent.SourcePlatform)}) хочет связать свой аккаунт " +
            "с вашим Telegram-профилем.\n\n" +
            "Подтвердите или отклоните запрос.\n" +
            $"Действует до {integrationEvent.ExpiresAt:dd.MM.yyyy HH:mm} UTC.";

        bool delivered;
        try
        {
            delivered = await sender.TrySendTextAsync(chatId, text, markup, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Не удалось отправить запрос связки в Telegram {ChatId}", chatId);
            await NotifyFailedAsync(integrationEvent.RequestId, ex.Message, cancellationToken);
            return;
        }

        if (!delivered)
            await NotifyFailedAsync(
                integrationEvent.RequestId,
                "Пользователь заблокировал бота или закрыл личные сообщения",
                cancellationToken);
    }

    /// <summary>Сообщает инициатору результат связки.</summary>
    /// <param name="integrationEvent">Событие результата связки.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача обработки.</returns>
    public async Task HandleAsync(
        LinkResultNotification integrationEvent,
        CancellationToken cancellationToken)
    {
        if (Platforms.Normalize(integrationEvent.SourcePlatform) != Platforms.Telegram)
            return;

        if (!long.TryParse(integrationEvent.SourceExternalId, out var chatId))
            return;

        var text = integrationEvent.Accepted
            ? "✅ Связка аккаунтов подтверждена. Теперь озвучку можно отправлять в оба сервиса."
            : $"❌ Связка аккаунтов не установлена.\n{integrationEvent.Reason}";

        try
        {
            await sender.TrySendTextAsync(chatId, text, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось отправить результат связки в Telegram {ChatId}", chatId);
        }
    }

    /// <summary>Доставляет готовую озвучку получателю в Telegram.</summary>
    /// <param name="integrationEvent">Событие доставки озвучки.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача обработки.</returns>
    public async Task HandleAsync(
        SpeechDeliveryRequested integrationEvent,
        CancellationToken cancellationToken)
    {
        if (Platforms.Normalize(integrationEvent.TargetPlatform) != Platforms.Telegram)
            return;

        if (!long.TryParse(integrationEvent.TargetExternalId, out var chatId))
            return;

        try
        {
            var delivered = await sender.TrySendVoiceAsync(
                chatId,
                integrationEvent.WavPath,
                integrationEvent.OggPath,
                integrationEvent.NotificationText ?? "Вот ваша озвучка:",
                cancellationToken);

            if (!delivered)
                logger.LogWarning("Telegram {ChatId} недоступен для доставки озвучки", chatId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Не удалось доставить озвучку в Telegram {ChatId}", chatId);
        }
    }

    private async Task NotifyFailedAsync(
        Guid requestId,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await identity.NotificationFailedAsync(requestId, reason, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось сообщить IdentityService об ошибке {RequestId}", requestId);
        }
    }
}
