using CurlyBot.Clients;
using CurlyBot.Contracts;
using CurlyBot.Contracts.Messaging;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace CurlyBot.DiscordBot.Services;

/// <summary>Обрабатывает нажатия кнопок подтверждения и отклонения связки в Discord.</summary>
/// <param name="identity">Клиент IdentityService.</param>
/// <param name="logger">Журнал обработчика.</param>
public sealed class LinkInteractionHandler(
    IdentityServiceClient identity,
    ILogger<LinkInteractionHandler> logger)
{
    /// <summary>Обрабатывает компонент-кнопку связки.</summary>
    /// <param name="component">Нажатая кнопка Discord.</param>
    /// <returns>Задача обработки.</returns>
    public async Task HandleAsync(SocketMessageComponent component)
    {
        var customId = component.Data.CustomId;
        var parts = customId.Split(':', 3);
        if (parts.Length != 3 || !Guid.TryParse(parts[2], out var requestId))
        {
            logger.LogWarning("Некорректный customId кнопки связки: {CustomId}", customId);
            return;
        }

        var accept = parts[1].Equals("accept", StringComparison.OrdinalIgnoreCase);
        var responderId = component.User.Id.ToString();

        try
        {
            var result = accept
                ? await identity.AcceptLinkAsync(requestId, Platforms.Discord, responderId)
                : await identity.DeclineLinkAsync(requestId, Platforms.Discord, responderId);

            await component.UpdateAsync(message =>
            {
                message.Content = (accept ? "✅ " : "❌ ") + result.Message;
                message.Embed = null;
                message.Components = new ComponentBuilder().Build();
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка обработки кнопки связки {RequestId}", requestId);

            try
            {
                await component.UpdateAsync(message =>
                {
                    message.Content = "Не удалось обработать запрос. Попробуйте позже.";
                    message.Embed = null;
                    message.Components = new ComponentBuilder().Build();
                });
            }
            catch
            {
                // interaction мог уже истечь
            }
        }
    }
}
