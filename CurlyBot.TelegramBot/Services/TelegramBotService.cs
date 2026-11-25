using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using CurlyBot.TelegramBot.Options;
using CurlyBot.TelegramBot.Services;
using Microsoft.Extensions.Options;

namespace CurlyBot.TelegramBot.Services;

/// <summary>
///     Long-polling цикл Telegram-бота. Регистрирует каждый контакт в IdentityService
///     и передаёт команды/кнопки в scoped-обработчики.
/// </summary>
public sealed class TelegramBotService(
    ITelegramBotClient bot,
    IServiceScopeFactory scopeFactory,
    IOptions<TelegramOptions> options,
    ILogger<TelegramBotService> logger) : BackgroundService
{
    private readonly TelegramOptions _options = options.Value;

    private static readonly BotCommand[] Commands =
    {
        new() { Command = "talk", Description = "Озвучить текст" },
        new() { Command = "linkme", Description = "Связать аккаунт с другим сервисом" },
        new() { Command = "unlinkme", Description = "Разорвать связку" },
        new() { Command = "linkstatus", Description = "Показать связки аккаунтов" },
        new() { Command = "linkuser", Description = "Связать пользователя (администратор)" },
        new() { Command = "unlinkuser", Description = "Разорвать связку пользователя (администратор)" },
        new() { Command = "cancel", Description = "Закрыть меню параметров" },
        new() { Command = "help", Description = "Справка" }
    };

    /// <summary>Запускает long-polling и регистрирует список команд бота.</summary>
    /// <param name="stoppingToken">Токен остановки службы.</param>
    /// <returns>Задача работы бота до остановки приложения.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Token))
            throw new InvalidOperationException("Telegram:Token не настроен.");

        var me = await bot.GetMe(stoppingToken);
        await bot.SetMyCommands(Commands, cancellationToken: stoppingToken);

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = new[] { UpdateType.Message, UpdateType.CallbackQuery },
            DropPendingUpdates = false
        };

        bot.StartReceiving(
            HandleUpdateAsync,
            HandleErrorAsync,
            receiverOptions,
            stoppingToken);

        logger.LogInformation("Telegram-бот @{Username} запущен", me.Username);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // штатная остановка
        }
    }

    private async Task HandleUpdateAsync(
        ITelegramBotClient botClient,
        Update update,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();

            if (update.CallbackQuery is { } callbackQuery)
            {
                var talkDialog = scope.ServiceProvider.GetRequiredService<TelegramTalkDialogHandler>();
                if (await talkDialog.HandleCallbackAsync(callbackQuery, cancellationToken))
                    return;

                var callbackHandler = scope.ServiceProvider.GetRequiredService<TelegramCallbackHandler>();
                await callbackHandler.HandleAsync(callbackQuery, cancellationToken);
                return;
            }

            if (update.Message is not { From: { } from } message)
                return;

            var sender = scope.ServiceProvider.GetRequiredService<TelegramSender>();
            await sender.EnsureAccountAsync(from, cancellationToken);

            var dialogs = scope.ServiceProvider.GetRequiredService<TalkDialogState>();
            var talk = scope.ServiceProvider.GetRequiredService<TelegramTalkDialogHandler>();
            var hadDraft = dialogs.Get(message.Chat.Id, from.Id) is not null;
            var isCommand = message.Text is { } draftText && IsKnownCommand(draftText);

            // Пока мастер открыт, текст (в том числе начинающийся с "/", но не команда)
            // считается ответом на текущий шаг.
            if (hadDraft && !isCommand && await talk.HandleMessageAsync(message, cancellationToken))
                return;

            if (message.Text is not { } text || !text.StartsWith('/'))
                return;

            var trimmed = text.TrimStart();
            if (trimmed.Equals("/cancel", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("/cancel@", StringComparison.OrdinalIgnoreCase))
            {
                await talk.CancelAsync(message, cancellationToken);
                return;
            }

            // Любая другая команда завершает незавершённый сценарий.
            if (hadDraft)
                dialogs.Remove(message.Chat.Id, from.Id);

            if (TelegramTalkDialogHandler.IsBareTalkCommand(text))
            {
                await talk.StartAsync(message, cancellationToken);
                return;
            }

            var commandHandler = scope.ServiceProvider.GetRequiredService<TelegramCommandHandler>();
            await commandHandler.HandleAsync(message, text, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // штатная остановка
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Необработанная ошибка Telegram update {UpdateId}",
                update.Id);
        }
    }

    private static bool IsKnownCommand(string text)
    {
        var name = text.TrimStart().Split(' ', 2)[0].ToLowerInvariant();
        var atIndex = name.IndexOf('@');
        if (atIndex > 0)
            name = name[..atIndex];

        return Commands.Any(x => string.Equals("/" + x.Command, name, StringComparison.Ordinal));
    }

    private Task HandleErrorAsync(
        ITelegramBotClient botClient,
        Exception exception,
        CancellationToken cancellationToken)
    {
        logger.LogWarning(exception, "Ошибка long-polling Telegram");
        return Task.CompletedTask;
    }
}
