using CurlyBot.Clients;
using CurlyBot.Contracts;
using CurlyBot.Contracts.Messaging;
using CurlyBot.Messaging;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace CurlyBot.DiscordBot.Services;

/// <summary>
///     Обрабатывает события связки и доставки, адресованные Discord-боту:
///     показывает кнопки подтверждения, сообщает результат и отправляет
///     озвучку, пришедшую из Telegram.
/// </summary>
public sealed class DiscordLinkEventsHandler(
    DiscordSocketClient client,
    IdentityServiceClient identity,
    ILogger<DiscordLinkEventsHandler> logger)
    : IIntegrationEventHandler<LinkConfirmationRequested>,
      IIntegrationEventHandler<LinkResultNotification>,
      IIntegrationEventHandler<SpeechDeliveryRequested>
{
    /// <summary>Отправляет получателю запрос на подтверждение связки.</summary>
    /// <param name="integrationEvent">Событие запроса связки.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача обработки.</returns>
    public async Task HandleAsync(LinkConfirmationRequested integrationEvent, CancellationToken cancellationToken)
    {
        if (Platforms.Normalize(integrationEvent.TargetPlatform) != Platforms.Discord)
            return;

        if (!ulong.TryParse(integrationEvent.TargetExternalId, out var userId))
        {
            await NotifyFailedAsync(integrationEvent.RequestId, "Некорректный Discord ID", cancellationToken);
            return;
        }

        try
        {
            await EnsureReadyAsync(cancellationToken);
            var user = await GetUserAsync(userId, cancellationToken);
            if (user is null)
            {
                await NotifyFailedAsync(integrationEvent.RequestId, "Пользователь Discord не найден", cancellationToken);
                return;
            }

            var dm = await user.CreateDMChannelAsync();

            var embed = new EmbedBuilder()
                .WithTitle("Запрос на связку аккаунтов")
                .WithDescription(
                    $"Пользователь **{integrationEvent.SourceDisplayName}** " +
                    $"({Platforms.DisplayName(integrationEvent.SourcePlatform)}) хочет связать свой аккаунт " +
                    "с вашим Discord-профилем.\n\nПодтвердите или отклоните запрос.")
                .WithColor(Color.Blue)
                .WithFooter($"Действует до {integrationEvent.ExpiresAt:dd.MM.yyyy HH:mm} UTC")
                .Build();

            var components = new ComponentBuilder()
                .WithButton("Подтвердить", $"link:accept:{integrationEvent.RequestId}", ButtonStyle.Success)
                .WithButton("Отклонить", $"link:decline:{integrationEvent.RequestId}", ButtonStyle.Danger)
                .Build();

            await dm.SendMessageAsync(embed: embed, components: components);
            await TouchSafeAsync(userId);
        }
        catch (Discord.Net.HttpException ex) when (ex.DiscordCode == DiscordErrorCode.CannotSendMessageToUser)
        {
            await MarkBlockedSafeAsync(userId);
            await NotifyFailedAsync(integrationEvent.RequestId, "У пользователя закрыты личные сообщения", cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Не удалось отправить запрос связки пользователю {UserId}", userId);
            await NotifyFailedAsync(integrationEvent.RequestId, ex.Message, cancellationToken);
        }
    }

    /// <summary>Сообщает инициатору результат связки.</summary>
    /// <param name="integrationEvent">Событие результата связки.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача обработки.</returns>
    public async Task HandleAsync(LinkResultNotification integrationEvent, CancellationToken cancellationToken)
    {
        if (Platforms.Normalize(integrationEvent.SourcePlatform) != Platforms.Discord)
            return;

        if (!ulong.TryParse(integrationEvent.SourceExternalId, out var userId))
            return;

        try
        {
            await EnsureReadyAsync(cancellationToken);
            var user = await GetUserAsync(userId, cancellationToken);
            if (user is null)
                return;

            var dm = await user.CreateDMChannelAsync();
            var text = integrationEvent.Accepted
                ? "✅ Связка аккаунтов подтверждена. Теперь озвучку можно отправлять в оба сервиса."
                : $"❌ Связка аккаунтов не установлена.\n{integrationEvent.Reason}";

            await dm.SendMessageAsync(text);
            await TouchSafeAsync(userId);
        }
        catch (Discord.Net.HttpException ex) when (ex.DiscordCode == DiscordErrorCode.CannotSendMessageToUser)
        {
            await MarkBlockedSafeAsync(userId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось отправить результат связки пользователю {UserId}", userId);
        }
    }

    /// <summary>Доставляет готовую озвучку в личные сообщения Discord.</summary>
    /// <param name="integrationEvent">Событие доставки озвучки.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача обработки.</returns>
    public async Task HandleAsync(SpeechDeliveryRequested integrationEvent, CancellationToken cancellationToken)
    {
        if (Platforms.Normalize(integrationEvent.TargetPlatform) != Platforms.Discord)
            return;

        if (!ulong.TryParse(integrationEvent.TargetExternalId, out var userId))
            return;

        try
        {
            await EnsureReadyAsync(cancellationToken);
            var user = await GetUserAsync(userId, cancellationToken);
            if (user is null)
            {
                logger.LogWarning("Получатель доставки {UserId} не найден в Discord", userId);
                return;
            }

            if (!File.Exists(integrationEvent.WavPath))
            {
                logger.LogError("Файл озвучки не найден: {Path}", integrationEvent.WavPath);
                return;
            }

            var dm = await user.CreateDMChannelAsync();
            var text = integrationEvent.NotificationText ?? "Вот ваша озвучка:";
            await dm.SendFileAsync(integrationEvent.WavPath, text);
            await TouchSafeAsync(userId);
        }
        catch (Discord.Net.HttpException ex) when (ex.DiscordCode == DiscordErrorCode.CannotSendMessageToUser)
        {
            await MarkBlockedSafeAsync(userId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Не удалось доставить озвучку в Discord пользователю {UserId}", userId);
        }
    }

    private async Task<IUser?> GetUserAsync(ulong userId, CancellationToken cancellationToken)
    {
        var cached = client.GetUser(userId);
        if (cached is not null)
            return cached;

        return await client.GetUserAsync(userId, new RequestOptions { RetryMode = RetryMode.AlwaysRetry });
    }

    private async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < 60 && client.ConnectionState != ConnectionState.Connected; i++)
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
    }

    private async Task NotifyFailedAsync(Guid requestId, string reason, CancellationToken cancellationToken)
    {
        try
        {
            await identity.NotificationFailedAsync(requestId, reason, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось сообщить IdentityService об ошибке доставки {RequestId}", requestId);
        }
    }

    private async Task TouchSafeAsync(ulong userId)
    {
        try
        {
            await identity.TouchContactAsync(Platforms.Discord, userId.ToString());
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Не удалось обновить контакт {UserId}", userId);
        }
    }

    private async Task MarkBlockedSafeAsync(ulong userId)
    {
        try
        {
            await identity.SetDmBlockedAsync(Platforms.Discord, userId.ToString(), true);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Не удалось отметить закрытые ЛС для {UserId}", userId);
        }
    }
}
