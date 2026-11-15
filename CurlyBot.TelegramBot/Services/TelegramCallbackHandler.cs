using CurlyBot.Clients;
using CurlyBot.Contracts;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace CurlyBot.TelegramBot.Services;

/// <summary>Обрабатывает inline-кнопки подтверждения/отклонения связки аккаунтов.</summary>
public sealed class TelegramCallbackHandler(
    ITelegramBotClient bot,
    IdentityServiceClient identity,
    ILogger<TelegramCallbackHandler> logger)
{
    /// <summary>Обрабатывает нажатие inline-кнопки связки.</summary>
    /// <param name="callbackQuery">Данные callback-запроса Telegram.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача обработки.</returns>
    public async Task HandleAsync(CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        var data = callbackQuery.Data;
        if (string.IsNullOrWhiteSpace(data) ||
            !data.StartsWith("link:", StringComparison.Ordinal))
            return;

        var parts = data.Split(':', 3);
        if (parts.Length != 3 || !Guid.TryParse(parts[2], out var requestId))
        {
            logger.LogWarning("Некорректный callback связки: {Data}", data);
            await AnswerAsync(callbackQuery.Id, "Некорректный запрос.", cancellationToken);
            return;
        }

        var accept = parts[1].Equals("accept", StringComparison.OrdinalIgnoreCase);
        var responderId = callbackQuery.From.Id.ToString();

        try
        {
            var result = accept
                ? await identity.AcceptLinkAsync(requestId, Platforms.Telegram, responderId, cancellationToken)
                : await identity.DeclineLinkAsync(requestId, Platforms.Telegram, responderId, cancellationToken);

            if (callbackQuery.Message is { } message)
            {
                try
                {
                    await bot.EditMessageText(
                        message.Chat.Id,
                        message.Id,
                        (accept ? "✅ " : "❌ ") + result.Message,
                        replyMarkup: null,
                        cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Не удалось изменить сообщение связки {RequestId}", requestId);
                }
            }

            await AnswerAsync(callbackQuery.Id, result.Message, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка обработки кнопки связки {RequestId}", requestId);
            await AnswerAsync(
                callbackQuery.Id,
                "Не удалось обработать запрос. Попробуйте позже.",
                cancellationToken);
        }
    }

    private async Task AnswerAsync(string callbackQueryId, string text, CancellationToken cancellationToken)
    {
        try
        {
            await bot.AnswerCallbackQuery(
                callbackQueryId,
                text,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Не удалось ответить на callback {CallbackId}", callbackQueryId);
        }
    }
}
