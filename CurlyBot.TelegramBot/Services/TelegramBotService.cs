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
                var callbackHandler = scope.ServiceProvider.GetRequiredService<TelegramCallbackHandler>();
                await callbackHandler.HandleAsync(callbackQuery, cancellationToken);
                return;
            }

            if (update.Message is not { From: { } from } message)
                return;

            var sender = scope.ServiceProvider.GetRequiredService<TelegramSender>();
            await sender.EnsureAccountAsync(from, cancellationToken);

            if (message.Text is { } text && text.StartsWith('/'))
            {
                var commandHandler = scope.ServiceProvider.GetRequiredService<TelegramCommandHandler>();
                await commandHandler.HandleAsync(message, text, cancellationToken);
            }
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

    private Task HandleErrorAsync(
        ITelegramBotClient botClient,
        Exception exception,
        CancellationToken cancellationToken)
    {
        logger.LogWarning(exception, "Ошибка long-polling Telegram");
        return Task.CompletedTask;
    }
}
