using CurlyBot.Clients;
using CurlyBot.Contracts;
using CurlyBot.Contracts.Identity;
using CurlyBot.TelegramBot.Options;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace CurlyBot.TelegramBot.Services;

/// <summary>
///     Пошаговый ввод параметров команды <c>/talk</c>: меню с inline-кнопками, ввод текста и
///     получателя обычными сообщениями, переключатели флагов и отправка готовой команды.
/// </summary>
public sealed class TelegramTalkDialogHandler(
    ITelegramBotClient bot,
    IdentityServiceClient identity,
    TalkDialogState dialogs,
    TelegramCommandHandler commands,
    TelegramSender sender,
    IOptions<TelegramOptions> options,
    ILogger<TelegramTalkDialogHandler> logger)
{
    private const string CallbackPrefix = "talk:";
    private const int MaxTargetSuggestions = 25;
    private const int MaxTargetButtons = 10;
    private const int ButtonTextLimit = 64;

    private readonly TelegramOptions _options = options.Value;

    /// <summary>Проверяет, что сообщение — это <c>/talk</c> без аргументов.</summary>
    /// <param name="text">Текст сообщения.</param>
    /// <returns><see langword="true"/>, если нужно открыть меню параметров.</returns>
    public static bool IsBareTalkCommand(string text)
    {
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith("/talk", StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = trimmed["/talk".Length..];
        if (rest.StartsWith('@'))
        {
            var spaceIndex = rest.IndexOf(' ');
            rest = spaceIndex < 0 ? string.Empty : rest[spaceIndex..];
        }

        return string.IsNullOrWhiteSpace(rest);
    }

    /// <summary>Начинает сценарий и отправляет меню параметров.</summary>
    /// <param name="commandMessage">Сообщение с командой <c>/talk</c>.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача обработки.</returns>
    public async Task StartAsync(Message commandMessage, CancellationToken cancellationToken)
    {
        if (commandMessage.From is null)
            return;

        var draft = dialogs.Start(commandMessage.Chat.Id, commandMessage.From.Id, commandMessage);
        var menu = BuildMenu(draft);
        var sent = await sender.TrySendTextMessageAsync(
            commandMessage.Chat.Id,
            menu.Text,
            menu.Keyboard,
            cancellationToken);

        if (sent is null)
        {
            dialogs.Remove(commandMessage.Chat.Id, commandMessage.From.Id);
            return;
        }

        draft.MenuMessageId = sent.Id;
        logger.LogInformation(
            "[TELEGRAM/TALK] Открыт мастер параметров: чат {ChatId}, пользователь {UserId}",
            commandMessage.Chat.Id,
            commandMessage.From.Id);
    }

    /// <summary>Закрывает меню параметров, если оно открыто.</summary>
    /// <param name="message">Сообщение с командой <c>/cancel</c>.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача обработки.</returns>
    public async Task CancelAsync(Message message, CancellationToken cancellationToken)
    {
        if (message.From is null)
            return;

        var draft = dialogs.Get(message.Chat.Id, message.From.Id);
        if (draft is null)
        {
            await sender.TrySendTextAsync(
                message.Chat.Id,
                "Нет активного меню параметров.",
                cancellationToken: cancellationToken);
            return;
        }

        dialogs.Remove(message.Chat.Id, message.From.Id);
        await EditAsync(draft.ChatId, draft.MenuMessageId, "Отменено.", null, cancellationToken);
        await sender.TrySendTextAsync(
            message.Chat.Id,
            "Меню параметров закрыто.",
            cancellationToken: cancellationToken);
    }

    /// <summary>Обрабатывает ответ пользователя на текущем шаге сценария.</summary>
    /// <param name="message">Сообщение пользователя.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns><see langword="true"/>, если сообщение относилось к сценарию.</returns>
    public async Task<bool> HandleMessageAsync(Message message, CancellationToken cancellationToken)
    {
        if (message.From is null)
            return false;

        var draft = dialogs.Get(message.Chat.Id, message.From.Id);
        if (draft is null)
            return false;

        dialogs.Touch(draft);

        if (message.Text is null)
        {
            draft.Hint = "Нужно именно текстовое сообщение.";
            await RenderStepAsync(draft, cancellationToken);
            return true;
        }

        var value = message.Text.Trim();

        switch (draft.Step)
        {
            case TalkDialogStep.Text:
                if (value.Length == 0)
                {
                    draft.Hint = "Текст пустой. Укажите, что озвучить.";
                    await RenderStepAsync(draft, cancellationToken);
                    return true;
                }

                if (value.Length > _options.MaxTextLength)
                {
                    draft.Hint = $"Текст слишком длинный: максимум {_options.MaxTextLength} символов, а получено {value.Length}.";
                    await RenderStepAsync(draft, cancellationToken);
                    return true;
                }

                draft.Text = value;
                break;

            case TalkDialogStep.Target:
                return await HandleTargetInputAsync(draft, value, cancellationToken);

            default:
                draft.Hint = "Выберите параметр кнопкой под сообщением с меню.";
                await RenderMenuAsync(draft, cancellationToken);
                return true;
        }

        dialogs.Touch(draft);
        draft.Step = TalkDialogStep.Menu;
        draft.Hint = null;
        await RenderMenuAsync(draft, cancellationToken);
        return true;
    }

    private async Task<bool> HandleTargetInputAsync(
        TalkDraft draft,
        string value,
        CancellationToken cancellationToken)
    {
        if (IsClearingTarget(value))
        {
            draft.TargetQuery = null;
            draft.TargetName = null;
            draft.Step = TalkDialogStep.Menu;
            draft.Hint = null;
            await RenderMenuAsync(draft, cancellationToken);
            return true;
        }

        var found = await identity.FindAccountsAsync(value, cancellationToken);
        var people = AccountPeople.Group(found.Accounts);

        // Один человек может иметь несколько связанных аккаунтов — это не неоднозначность.
        if (people.Count == 1)
        {
            draft.TargetQuery = AccountQuery.ToReference(
                AccountPeople.Representative(people[0], Platforms.Telegram));
            draft.TargetName = AccountPeople.Describe(people[0], Platforms.Telegram);
            draft.Step = TalkDialogStep.Menu;
            draft.Hint = null;
            await RenderMenuAsync(draft, cancellationToken);
            return true;
        }

        var suggestions = people.Count > 1
            ? found.Accounts
            : await identity.SuggestAccountsAsync(value, MaxTargetSuggestions, cancellationToken);

        draft.Hint = people.Count > 1
            ? "Найдено несколько человек. Выберите нужного кнопкой."
            : suggestions.Count == 0
                ? $"«{value}» не найден. Укажите @username, ID или ссылку."
                : null;

        await RenderTargetStepAsync(draft, value, suggestions, cancellationToken);
        return true;
    }

    private async Task<bool> PickTargetAsync(
        TalkDraft draft,
        string reference,
        CancellationToken cancellationToken)
    {
        var parts = reference.Split(':', 2);
        if (parts.Length != 2 || parts[1].Length == 0)
            return false;

        var platform = Platforms.Normalize(parts[0]);
        if (platform is not (Platforms.Telegram or Platforms.Discord))
            return false;

        var found = await identity.FindAccountsAsync($"{platform}:{parts[1]}", cancellationToken);
        var account = found.Accounts.FirstOrDefault(x =>
            string.Equals(x.Platform, platform, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.ExternalId, parts[1], StringComparison.Ordinal));
        if (account is null)
            return false;

        draft.TargetQuery = AccountQuery.ToReference(account);
        draft.TargetName = $"{account.DisplayName} (@{account.Username})";
        draft.Step = TalkDialogStep.Menu;
        draft.Hint = null;
        await RenderMenuAsync(draft, cancellationToken);
        return true;
    }

    /// <summary>Обрабатывает нажатия inline-кнопок мастера параметров.</summary>
    /// <param name="callbackQuery">Данные callback-запроса Telegram.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns><see langword="true"/>, если кнопка относилась к сценарию <c>/talk</c>.</returns>
    public async Task<bool> HandleCallbackAsync(CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        var data = callbackQuery.Data;
        if (string.IsNullOrWhiteSpace(data) ||
            !data.StartsWith(CallbackPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var action = data[CallbackPrefix.Length..];
        var message = callbackQuery.Message;
        var chatId = message?.Chat.Id ?? 0;

        var draft = dialogs.Get(chatId, callbackQuery.From.Id);
        if (draft is null || message is null)
        {
            if (message is not null)
            {
                await EditAsync(
                    chatId,
                    message.Id,
                    "Сценарий завершён. Нажмите /talk, чтобы начать заново.",
                    null,
                    cancellationToken);
            }

            await AnswerAsync(
                callbackQuery.Id,
                "Сценарий завершён. Нажмите /talk, чтобы начать заново.",
                cancellationToken);
            return true;
        }

        var answered = false;

        if (action.StartsWith("pick:", StringComparison.OrdinalIgnoreCase))
        {
            var picked = await PickTargetAsync(draft, action["pick:".Length..], cancellationToken);
            await AnswerAsync(
                callbackQuery.Id,
                picked ? null : "Не удалось выбрать получателя.",
                cancellationToken);

            if (picked)
                dialogs.Touch(draft);

            return true;
        }

        switch (action)
        {
            case "field:text":
                draft.Step = TalkDialogStep.Text;
                draft.Hint = null;
                await RenderStepAsync(draft, cancellationToken);
                break;

            case "field:target":
                draft.Step = TalkDialogStep.Target;
                draft.Hint = null;
                await RenderTargetStepAsync(draft, string.Empty, null, cancellationToken);
                break;

            case "toggle:personal":
                draft.Personal = !draft.Personal;
                await RenderMenuAsync(draft, cancellationToken);
                break;

            case "toggle:current":
                draft.CurrentOnly = !draft.CurrentOnly;
                await RenderMenuAsync(draft, cancellationToken);
                break;

            case "back":
                draft.Step = TalkDialogStep.Menu;
                draft.Hint = null;
                await RenderMenuAsync(draft, cancellationToken);
                break;

            case "cancel":
                dialogs.Remove(chatId, callbackQuery.From.Id);
                await EditAsync(chatId, message.Id, "Отменено.", null, cancellationToken);
                await AnswerAsync(callbackQuery.Id, "Отменено.", cancellationToken);
                answered = true;
                break;

            case "send":
                if (string.IsNullOrWhiteSpace(draft.Text))
                {
                    await AnswerAsync(callbackQuery.Id, "Сначала укажите текст.", cancellationToken);
                    answered = true;
                    break;
                }

                var targetQuery = draft.TargetQuery;
                var text = draft.Text;
                var personal = draft.Personal;
                var currentOnly = draft.CurrentOnly;
                dialogs.Remove(chatId, callbackQuery.From.Id);
                await EditAsync(chatId, message.Id, "Отправляем озвучку…", null, cancellationToken);
                await AnswerAsync(callbackQuery.Id, "Отправляем…", cancellationToken);
                answered = true;

                logger.LogInformation(
                    "[TELEGRAM/TALK] Отправка через мастер: чат {ChatId}, пользователь {UserId}",
                    chatId,
                    callbackQuery.From.Id);
                await commands.ExecuteTalkAsync(
                    draft.CommandMessage,
                    targetQuery,
                    text!,
                    personal,
                    currentOnly,
                    cancellationToken);
                break;

            default:
                await AnswerAsync(callbackQuery.Id, "Неизвестная кнопка.", cancellationToken);
                answered = true;
                break;
        }

        dialogs.Touch(draft);
        if (!answered)
            await AnswerAsync(callbackQuery.Id, null, cancellationToken);

        return true;
    }

    private async Task RenderMenuAsync(TalkDraft draft, CancellationToken cancellationToken)
    {
        var menu = BuildMenu(draft);
        await EditAsync(draft.ChatId, draft.MenuMessageId, menu.Text, menu.Keyboard, cancellationToken);
    }

    private async Task RenderStepAsync(TalkDraft draft, CancellationToken cancellationToken)
    {
        var text =
            "✏️ Введите текст озвучки\n\n" +
            $"Отправьте текст обычным сообщением — до {_options.MaxTextLength} символов.";

        if (!string.IsNullOrWhiteSpace(draft.Hint))
            text += "\n\n⚠️ " + draft.Hint;

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("« Назад", "talk:back"),
                InlineKeyboardButton.WithCallbackData("❌ Отмена", "talk:cancel")
            }
        });

        await EditAsync(draft.ChatId, draft.MenuMessageId, text, keyboard, cancellationToken);
        draft.Hint = null;
    }

    private async Task RenderTargetStepAsync(
        TalkDraft draft,
        string query,
        IReadOnlyList<AccountDto>? suggestions,
        CancellationToken cancellationToken)
    {
        suggestions ??= await identity.SuggestAccountsAsync(query, MaxTargetSuggestions, cancellationToken);

        var people = AccountPeople.Group(suggestions)
            .Select(person => new
            {
                Person = person,
                Representative = AccountPeople.Representative(person, Platforms.Telegram)
            })
            .ToList();

        var text = "👤 Кому озвучить?\n\n";

        if (people.Count == 0)
        {
            text += string.IsNullOrWhiteSpace(query)
                ? "В базе пока нет зарегистрированных пользователей. Отправьте @username, ID или ссылку — либо озвучьте себе, нажав «Назад»."
                : $"По запросу «{query}» никого не найдено. Отправьте @username, ID или ссылку, чтобы уточнить.";
        }
        else
        {
            text += string.IsNullOrWhiteSpace(query)
                ? "Выберите человека из списка или отправьте сообщение с @username, ID либо ссылкой — список отфильтруется по мере ввода."
                : $"Поиск по «{query}». Выберите человека или продолжите ввод.";
        }

        if (people.Count > MaxTargetButtons)
            text += $"\n\nПоказаны первые {MaxTargetButtons}. Уточните запрос, чтобы сузить список.";

        if (!string.IsNullOrWhiteSpace(draft.Hint))
            text += "\n\n⚠️ " + draft.Hint;

        var rows = new List<InlineKeyboardButton[]>();
        foreach (var choice in people.Take(MaxTargetButtons))
        {
            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    Truncate(AccountPeople.Describe(choice.Person, Platforms.Telegram), ButtonTextLimit),
                    $"talk:pick:{choice.Representative.Platform}:{choice.Representative.ExternalId}")
            });
        }

        rows.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData("« Назад", "talk:back"),
            InlineKeyboardButton.WithCallbackData("❌ Отмена", "talk:cancel")
        });

        await EditAsync(
            draft.ChatId,
            draft.MenuMessageId,
            text,
            new InlineKeyboardMarkup(rows),
            cancellationToken);
        draft.Hint = null;
    }

    private static (string Text, InlineKeyboardMarkup Keyboard) BuildMenu(TalkDraft draft)
    {
        var target = string.IsNullOrWhiteSpace(draft.TargetQuery)
            ? "себе (этот чат)"
            : draft.TargetName ?? draft.TargetQuery;

        var text =
            "🎙 Озвучить текст\n\n" +
            $"Текст: {(draft.Text is null ? "не задан" : Shorten(draft.Text, 120))}\n" +
            $"Кому: {target}\n" +
            $"Личное сообщение: {(draft.Personal ? "да" : "нет")}\n" +
            $"Только текущий сервис: {(draft.CurrentOnly ? "да" : "нет")}\n\n" +
            (draft.Text is null
                ? "Сначала заполните «Текст», затем нажмите «Озвучить»."
                : "Всё готово — нажмите «Озвучить».");

        if (!string.IsNullOrWhiteSpace(draft.Hint))
            text += "\n\n⚠️ " + draft.Hint;

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    draft.Text is null ? "✏️ Текст: не задан" : "✏️ Текст: готов",
                    "talk:field:text"),
                InlineKeyboardButton.WithCallbackData(
                    draft.TargetQuery is null ? "👤 Кому: себе" : "👤 Кому: задан",
                    "talk:field:target")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    $"✉️ Личное: {(draft.Personal ? "да" : "нет")}",
                    "talk:toggle:personal"),
                InlineKeyboardButton.WithCallbackData(
                    $"🌐 Только здесь: {(draft.CurrentOnly ? "да" : "нет")}",
                    "talk:toggle:current")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("▶️ Озвучить", "talk:send"),
                InlineKeyboardButton.WithCallbackData("❌ Отмена", "talk:cancel")
            }
        });

        return (text, keyboard);
    }

    private static bool IsClearingTarget(string value)
        => value is "-" or "себе" or "СЕБЕ" or "self" or "clear";

    private static string Shorten(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "…";

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";

    private async Task EditAsync(
        long chatId,
        int messageId,
        string text,
        InlineKeyboardMarkup? keyboard,
        CancellationToken cancellationToken)
    {
        try
        {
            await bot.EditMessageText(
                chatId,
                messageId,
                text,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Не удалось обновить сообщение мастера /talk {MessageId} в чате {ChatId}",
                messageId,
                chatId);
        }
    }

    private async Task AnswerAsync(
        string callbackQueryId,
        string? text,
        CancellationToken cancellationToken)
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
            logger.LogDebug(ex, "Не удалось ответить на callback мастера /talk {CallbackId}", callbackQueryId);
        }
    }
}
