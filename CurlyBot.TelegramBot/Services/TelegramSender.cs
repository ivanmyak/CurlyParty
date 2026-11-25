using CurlyBot.Clients;
using CurlyBot.Contracts;
using CurlyBot.Contracts.Identity;
using CurlyBot.Contracts.Models;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace CurlyBot.TelegramBot.Services;

/// <summary>
///     Единая точка отправки сообщений в Telegram. Умеет отличать «бот заблокирован»
///     от прочих ошибок и отмечает такие аккаунты в IdentityService.
/// </summary>
public sealed class TelegramSender(
    ITelegramBotClient bot,
    IdentityServiceClient identity,
    ILogger<TelegramSender> logger)
{
    /// <summary>Отправляет текстовое сообщение в чат.</summary>
    /// <param name="chatId">Идентификатор чата.</param>
    /// <param name="text">Текст сообщения.</param>
    /// <param name="replyMarkup">Необязательная клавиатура с кнопками.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Отправленное сообщение.</returns>
    public async Task<Message> SendTextAsync(
        long chatId,
        string text,
        ReplyMarkup? replyMarkup = null,
        CancellationToken cancellationToken = default)
        => await bot.SendMessage(
            chatId,
            text,
            replyMarkup: replyMarkup,
            cancellationToken: cancellationToken);

    /// <summary>Пытается отправить текст и отмечает чат недоступным при блокировке бота.</summary>
    /// <param name="chatId">Идентификатор чата.</param>
    /// <param name="text">Текст сообщения.</param>
    /// <param name="replyMarkup">Необязательная клавиатура с кнопками.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Отправленное сообщение или <see langword="null"/>, если чат недоступен.</returns>
    public async Task<Message?> TrySendTextMessageAsync(
        long chatId,
        string text,
        ReplyMarkup? replyMarkup = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var message = await SendTextAsync(chatId, text, replyMarkup, cancellationToken);
            await TouchAsync(chatId, cancellationToken);
            return message;
        }
        catch (ApiRequestException ex) when (IsUnreachable(ex))
        {
            logger.LogWarning(
                "Telegram не доставил сообщение в чат {ChatId}: {Error}",
                chatId,
                ex.Message);
            await MarkBlockedAsync(chatId, cancellationToken);
            return null;
        }
    }

    /// <summary>Пытается отправить текст и отмечает чат недоступным при блокировке бота.</summary>
    /// <param name="chatId">Идентификатор чата.</param>
    /// <param name="text">Текст сообщения.</param>
    /// <param name="replyMarkup">Необязательная клавиатура с кнопками.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns><see langword="true"/>, если сообщение доставлено.</returns>
    public async Task<bool> TrySendTextAsync(
        long chatId,
        string text,
        ReplyMarkup? replyMarkup = null,
        CancellationToken cancellationToken = default)
        => await TrySendTextMessageAsync(chatId, text, replyMarkup, cancellationToken) is not null;

    /// <summary>Отправляет голосовое сообщение, предпочитая OGG-файл.</summary>
    /// <param name="chatId">Идентификатор чата.</param>
    /// <param name="wavPath">Путь к WAV-файлу.</param>
    /// <param name="oggPath">Путь к OGG-файлу, если он есть.</param>
    /// <param name="caption">Необязательная подпись к голосовому сообщению.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns><see langword="true"/>, если сообщение доставлено.</returns>
    /// <exception cref="FileNotFoundException">Файл озвучки не найден.</exception>
    public async Task<bool> TrySendVoiceAsync(
        long chatId,
        string wavPath,
        string? oggPath,
        string? caption,
        CancellationToken cancellationToken = default)
    {
        var path = !string.IsNullOrWhiteSpace(oggPath) && File.Exists(oggPath)
            ? oggPath
            : wavPath;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("Файл озвучки не найден.", path);

        try
        {
            await using var stream = File.OpenRead(path);
            var file = InputFile.FromStream(stream, Path.GetFileName(path));

            await bot.SendVoice(
                chatId,
                file,
                caption: Truncate(caption, 1000),
                cancellationToken: cancellationToken);

            await TouchAsync(chatId, cancellationToken);
            return true;
        }
        catch (ApiRequestException ex) when (IsUnreachable(ex))
        {
            logger.LogWarning(
                "Telegram не доставил голосовое в чат {ChatId}: {Error}",
                chatId,
                ex.Message);
            await MarkBlockedAsync(chatId, cancellationToken);
            return false;
        }
    }

    /// <summary>Регистрирует или обновляет аккаунт Telegram-пользователя.</summary>
    /// <param name="user">Пользователь Telegram.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Сохранённый аккаунт.</returns>
    public async Task<AccountDto> EnsureAccountAsync(User user, CancellationToken cancellationToken)
    {
        var username = string.IsNullOrWhiteSpace(user.Username)
            ? user.Id.ToString()
            : user.Username;

        var displayName = string.Join(
            " ",
            new[] { user.FirstName, user.LastName }.Where(x => !string.IsNullOrWhiteSpace(x)));

        if (string.IsNullOrWhiteSpace(displayName))
            displayName = username;

        return await identity.UpsertAccountAsync(
            Platforms.Telegram,
            user.Id.ToString(),
            username,
            displayName,
            cancellationToken);
    }

    /// <summary>Преобразует пользователя Telegram в платформо-независимое описание.</summary>
    /// <param name="user">Пользователь Telegram.</param>
    /// <returns>Описание пользователя для команд и событий.</returns>
    public PlatformUser ToPlatformUser(User user)
    {
        var username = string.IsNullOrWhiteSpace(user.Username)
            ? user.Id.ToString()
            : user.Username;

        var displayName = string.Join(
            " ",
            new[] { user.FirstName, user.LastName }.Where(x => !string.IsNullOrWhiteSpace(x)));

        if (string.IsNullOrWhiteSpace(displayName))
            displayName = username;

        return PlatformUser.Telegram(user.Id.ToString(), displayName, username);
    }

    /// <summary>Обновляет время последнего контакта пользователя (ошибки игнорируются).</summary>
    /// <param name="chatId">Идентификатор чата.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача обновления.</returns>
    public async Task TouchAsync(long chatId, CancellationToken cancellationToken = default)
    {
        try
        {
            await identity.TouchContactAsync(Platforms.Telegram, chatId.ToString(), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Не удалось обновить контакт Telegram {ChatId}", chatId);
        }
    }

    /// <summary>Отмечает чат как недоступный для бота (ошибки игнорируются).</summary>
    /// <param name="chatId">Идентификатор чата.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача обновления.</returns>
    public async Task MarkBlockedAsync(long chatId, CancellationToken cancellationToken = default)
    {
        try
        {
            await identity.SetDmBlockedAsync(
                Platforms.Telegram,
                chatId.ToString(),
                true,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Не удалось отметить закрытый чат Telegram {ChatId}", chatId);
        }
    }

    /// <summary>Определяет, что чат недоступен: бот заблокирован или чат не найден.</summary>
    /// <param name="ex">Исключение Telegram API.</param>
    /// <returns><see langword="true"/>, если сообщение доставить невозможно.</returns>
    public static bool IsUnreachable(ApiRequestException ex)
        => ex.ErrorCode == 403 ||
           (ex.ErrorCode == 400 &&
            ex.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase));

    private static string? Truncate(string? value, int maxLength)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength
            ? value
            : value[..maxLength];
}
