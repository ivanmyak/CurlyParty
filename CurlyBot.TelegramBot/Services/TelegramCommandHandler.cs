using CurlyBot.Clients;
using CurlyBot.Contracts;
using CurlyBot.Contracts.Identity;
using CurlyBot.TelegramBot.Options;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace CurlyBot.TelegramBot.Services;

/// <summary>
///     Обрабатывает текстовые команды Telegram-бота: /talk, /linkme, /linkuser,
///     /unlinkme, /unlinkuser, /linkstatus, /start и /help.
/// </summary>
public sealed class TelegramCommandHandler(
    ITelegramBotClient bot,
    IdentityServiceClient identity,
    SpeechSynthesisClient speech,
    CrossPlatformDeliveryClient delivery,
    TelegramSender sender,
    IOptions<TelegramOptions> options,
    ILogger<TelegramCommandHandler> logger)
{
    private readonly TelegramOptions _options = options.Value;

    private static readonly HashSet<string> ReferencePrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "@", "tg:", "telegram:", "discord:", "ds:", "http://", "https://", "tg://"
    };

    /// <summary>Разбирает и выполняет команду Telegram-бота.</summary>
    /// <param name="message">Сообщение с командой.</param>
    /// <param name="text">Полный текст сообщения.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача обработки команды.</returns>
    public async Task HandleAsync(Message message, string text, CancellationToken cancellationToken)
    {
        if (message.From is null)
            return;

        var (command, args) = SplitCommand(text);
        if (command.Length == 0)
            return;

        try
        {
            switch (command)
            {
                case "/start":
                    await HandleStartAsync(message, cancellationToken);
                    break;
                case "/help":
                    await HandleHelpAsync(message, cancellationToken);
                    break;
                case "/talk":
                    await HandleTalkAsync(message, args, cancellationToken);
                    break;
                case "/linkme":
                    await HandleLinkMeAsync(message, args, cancellationToken);
                    break;
                case "/unlinkme":
                    await HandleUnlinkMeAsync(message, cancellationToken);
                    break;
                case "/linkuser":
                    await HandleLinkUserAsync(message, args, cancellationToken);
                    break;
                case "/unlinkuser":
                    await HandleUnlinkUserAsync(message, args, cancellationToken);
                    break;
                case "/linkstatus":
                    await HandleLinkStatusAsync(message, args, cancellationToken);
                    break;
                default:
                    if (message.Chat.Type == ChatType.Private)
                        await ReplyAsync(message, "Неизвестная команда. Используйте /help.", cancellationToken);
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка обработки команды {Command} от {UserId}", command, message.From.Id);
            await ReplyAsync(message, "Не удалось выполнить команду. Попробуйте позже.", cancellationToken);
        }
    }

    private async Task HandleStartAsync(Message message, CancellationToken cancellationToken)
    {
        if (message.From is { } user)
            await sender.EnsureAccountAsync(user, cancellationToken);

        var text =
            "Привет! Я CurlyBot — озвучиваю текст голосом.\n\n" +
            "Команды:\n" +
            "/talk <текст> — озвучить себе (в группе — в этот чат)\n" +
            "/talk <@username|ссылка|ID> <текст> [true|false] [true|false] — озвучить другому пользователю\n" +
            "/linkme <@username|ссылка|ID> — связать ваш Telegram с аккаунтом в другом сервисе\n" +
            "/unlinkme — разорвать связку\n" +
            "/linkstatus — показать текущие связки\n\n" +
            "У /talk последние два флага: личное сообщение (true/false) и «только текущий сервис» (true/false).\n" +
            "Например: /talk @nick Привет false false — озвучить в Telegram и отправить копию в Discord.";

        await ReplyAsync(message, text, cancellationToken);
    }

    private Task HandleHelpAsync(Message message, CancellationToken cancellationToken)
        => HandleStartAsync(message, cancellationToken);

    private async Task HandleTalkAsync(Message message, string args, CancellationToken cancellationToken)
    {
        var callerUser = message.From!;
        var caller = await sender.EnsureAccountAsync(callerUser, cancellationToken);
        var callerPlatformUser = sender.ToPlatformUser(callerUser);

        var (tokens, personal, currentOnly) = ParseFlags(args);

        if (tokens.Count == 0)
        {
            await ReplyAsync(
                message,
                "Использование: /talk <текст> или /talk <@username|ссылка|ID> <текст> [личное] [только-текущий-сервис]",
                cancellationToken);
            return;
        }

        AccountDto? target = null;

        if (LooksLikeReference(tokens[0]))
        {
            var resolved = await ResolveSingleAsync(tokens[0], cancellationToken);
            if (resolved.Error is not null)
            {
                await ReplyAsync(message, resolved.Error, cancellationToken);
                return;
            }

            target = resolved.Account;
            tokens.RemoveAt(0);
        }
        else if (tokens.Count > 1)
        {
            // Если первый токен — известный ник из базы, считаем его получателем.
            var found = await identity.FindAccountsAsync(tokens[0], cancellationToken);
            if (found.Accounts.Count == 1)
            {
                target = found.Accounts[0];
                tokens.RemoveAt(0);
            }
        }

        var text = string.Join(" ", tokens).Trim();
        if (text.Length == 0)
        {
            await ReplyAsync(message, "Текст пустой. Укажите, что озвучить.", cancellationToken);
            return;
        }

        if (text.Length > _options.MaxTextLength)
        {
            await ReplyAsync(
                message,
                $"Текст слишком длинный (максимум {_options.MaxTextLength} символов, а в сообщении {text.Length}).",
                cancellationToken);
            return;
        }

        ProfileDto profile;
        try
        {
            profile = target is null
                ? await identity.GetProfileAsync(Platforms.Telegram, caller.ExternalId, cancellationToken)
                  ?? new ProfileDto(caller, Array.Empty<AccountDto>())
                : await identity.GetProfileAsync(target.Platform, target.ExternalId, cancellationToken)
                  ?? new ProfileDto(target, Array.Empty<AccountDto>());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Не удалось получить профиль получателя {Target}", target?.ExternalId);
            await ReplyAsync(message, "Сервис пользователей временно недоступен. Попробуйте позже.", cancellationToken);
            return;
        }

        var effectiveTarget = target ?? caller;
        var isSelf = effectiveTarget.Platform == Platforms.Telegram &&
                     effectiveTarget.ExternalId == caller.ExternalId;

        logger.LogInformation(
            "[TELEGRAM/TALK] Caller={Caller}({CallerId}), Target={TargetPlatform}/{TargetId} ({TargetName}), PM={PM}, CurrentOnly={CurrentOnly}, Text=\"{Text}\"",
            caller.DisplayName,
            caller.ExternalId,
            effectiveTarget.Platform,
            effectiveTarget.ExternalId,
            effectiveTarget.DisplayName,
            personal,
            currentOnly,
            text);

        var messages = new List<string>();
        SpeechSynthesisResult speechResult;

        try
        {
            speechResult = await speech.SynthesizeAsync(text, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Не удалось синтезировать речь для Telegram");
            await ReplyAsync(message, "Сервис озвучки временно недоступен. Попробуйте позже.", cancellationToken);
            return;
        }

        var notification = isSelf
            ? "Вы отправили сообщение самому себе:"
            : $"{callerPlatformUser.DisplayName} сказал вам:";

        var targetDelivered = await DeliverToAccountAsync(
            message.Chat,
            effectiveTarget,
            speechResult,
            isSelf,
            personal,
            notification,
            callerPlatformUser,
            cancellationToken);

        messages.Add(targetDelivered
            ? isSelf
                ? "Озвучка отправлена в этот чат."
                : $"Озвучка отправлена пользователю {effectiveTarget.DisplayName}."
            : "Не удалось доставить озвучку получателю.");

        if (!currentOnly)
        {
            var linked = profile.LinkedAccounts.FirstOrDefault(x =>
                !string.Equals(x.Platform, effectiveTarget.Platform, StringComparison.OrdinalIgnoreCase) ||
                x.ExternalId != effectiveTarget.ExternalId);

            if (linked is null)
            {
                messages.Add("Связанный аккаунт на другой платформе не найден.");
            }
            else
            {
                var linkedDelivered = await DeliverToAccountAsync(
                    message.Chat,
                    linked,
                    speechResult,
                    isSelf,
                    personal,
                    notification,
                    callerPlatformUser,
                    cancellationToken);

                messages.Add(linkedDelivered
                    ? $"Копия отправлена связанному аккаунту {linked.DisplayName} в {Platforms.DisplayName(linked.Platform)}."
                    : $"Не удалось отправить копию в {Platforms.DisplayName(linked.Platform)}.");
            }
        }

        await ReplyAsync(message, string.Join("\n", messages), cancellationToken);
    }

    private async Task<bool> DeliverToAccountAsync(
        Chat commandChat,
        AccountDto target,
        SpeechSynthesisResult speechResult,
        bool isSelf,
        bool personal,
        string notification,
        Contracts.Models.PlatformUser caller,
        CancellationToken cancellationToken)
    {
        try
        {
            if (target.Platform == Platforms.Telegram)
            {
                if (!long.TryParse(target.ExternalId, out var targetChatId))
                    return false;

                var chatId = targetChatId;
                if (isSelf && !personal && commandChat.Type is ChatType.Group or ChatType.Supergroup)
                    chatId = commandChat.Id;

                var caption = isSelf && chatId == targetChatId && commandChat.Type == ChatType.Private
                    ? null
                    : notification;

                return await sender.TrySendVoiceAsync(
                    chatId,
                    speechResult.WavPath,
                    speechResult.OggPath,
                    caption,
                    cancellationToken);
            }

            await delivery.DeliverAsync(
                target.Platform,
                target.ExternalId,
                target.DisplayName,
                caller.DisplayName,
                isSelf ? "Вы отправили сообщение самому себе:" : $"{caller.DisplayName} сказал вам:",
                speechResult,
                cancellationToken);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Не удалось доставить озвучку в {Platform} пользователю {TargetId}",
                target.Platform,
                target.ExternalId);
            return false;
        }
    }

    private async Task HandleLinkMeAsync(Message message, string args, CancellationToken cancellationToken)
    {
        var user = message.From!;
        if (string.IsNullOrWhiteSpace(args))
        {
            await ReplyAsync(
                message,
                "Использование: /linkme <@username, ссылка на профиль или ID аккаунта в другом сервисе>",
                cancellationToken);
            return;
        }

        await sender.EnsureAccountAsync(user, cancellationToken);

        var targetPlatform = AccountQuery.ResolveLinkTargetPlatform(args, Platforms.Telegram);
        var result = await identity.CreateLinkRequestAsync(
            Platforms.Telegram,
            user.Id.ToString(),
            targetPlatform,
            args,
            cancellationToken);

        await ReplyAsync(message, result.Message, cancellationToken);
    }

    private async Task HandleUnlinkMeAsync(Message message, CancellationToken cancellationToken)
    {
        var user = message.From!;
        var result = await identity.UnlinkAsync(Platforms.Telegram, user.Id.ToString(), cancellationToken);
        await ReplyAsync(message, result.Message, cancellationToken);
    }

    private async Task HandleLinkUserAsync(Message message, string args, CancellationToken cancellationToken)
    {
        if (!await IsAdminAsync(message, cancellationToken))
        {
            await ReplyAsync(message, "Команда доступна только администратору.", cancellationToken);
            return;
        }

        var tokens = SplitArgs(args);
        if (tokens.Count < 2)
        {
            await ReplyAsync(
                message,
                "Использование: /linkuser <пользователь> <@username|ссылка|ID другого сервиса>\n" +
                "Например: /linkuser @telegram_user @discord_user",
                cancellationToken);
            return;
        }

        var sourceQuery = tokens[0];
        var targetQuery = string.Join(" ", tokens.Skip(1));
        var source = await ResolveSourceAccountAsync(sourceQuery, cancellationToken);
        if (source.Error is not null)
        {
            await ReplyAsync(message, source.Error, cancellationToken);
            return;
        }

        var targetPlatform = AccountQuery.ResolveLinkTargetPlatform(targetQuery, source.Account!.Platform);
        var result = await identity.AdminLinkAsync(
            source.Account.Platform,
            source.Account.ExternalId,
            targetPlatform,
            targetQuery,
            cancellationToken);

        logger.LogInformation(
            "[TELEGRAM/LINK] Администратор {Admin} связал {Source} с {Target} ({Status})",
            message.From!.Id,
            source.Account.ExternalId,
            targetQuery,
            result.Status);

        await ReplyAsync(message, result.Message, cancellationToken);
    }

    private async Task HandleUnlinkUserAsync(Message message, string args, CancellationToken cancellationToken)
    {
        if (!await IsAdminAsync(message, cancellationToken))
        {
            await ReplyAsync(message, "Команда доступна только администратору.", cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(args))
        {
            await ReplyAsync(message, "Использование: /unlinkuser <@username|ссылка|ID>", cancellationToken);
            return;
        }

        var resolved = await ResolveSingleAsync(args.Trim(), cancellationToken);
        if (resolved.Error is not null)
        {
            await ReplyAsync(message, resolved.Error, cancellationToken);
            return;
        }

        var result = await identity.UnlinkAsync(
            resolved.Account!.Platform,
            resolved.Account.ExternalId,
            cancellationToken);

        await ReplyAsync(message, result.Message, cancellationToken);
    }

    private async Task HandleLinkStatusAsync(Message message, string args, CancellationToken cancellationToken)
    {
        var user = message.From!;
        var isAdmin = await IsAdminAsync(message, cancellationToken);

        AccountDto account;
        if (string.IsNullOrWhiteSpace(args))
        {
            account = await sender.EnsureAccountAsync(user, cancellationToken);
        }
        else
        {
            if (!isAdmin)
            {
                await ReplyAsync(
                    message,
                    "Просматривать чужие связки может только администратор.",
                    cancellationToken);
                return;
            }

            var resolved = await ResolveSingleAsync(args.Trim(), cancellationToken);
            if (resolved.Error is not null)
            {
                await ReplyAsync(message, resolved.Error, cancellationToken);
                return;
            }

            account = resolved.Account!;
        }

        var profile = await identity.GetProfileAsync(account.Platform, account.ExternalId, cancellationToken);
        if (profile is null)
        {
            await ReplyAsync(message, "Профиль не найден в базе.", cancellationToken);
            return;
        }

        if (profile.LinkedAccounts.Count == 0)
        {
            await ReplyAsync(
                message,
                $"{account.DisplayName}: связанных аккаунтов нет.\n" +
                "Используйте /linkme <@username или ссылка>, чтобы создать связку.",
                cancellationToken);
            return;
        }

        var lines = profile.LinkedAccounts.Select(x =>
            $"- {Platforms.DisplayName(x.Platform)}: {x.DisplayName} (@{x.Username})" +
            (x.DmBlocked ? " — недоступен" : string.Empty));

        await ReplyAsync(
            message,
            $"Связки {account.DisplayName}:\n{string.Join("\n", lines)}",
            cancellationToken);
    }

    private async Task<(AccountDto? Account, string? Error)> ResolveSourceAccountAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var explicitPlatform = AccountQuery.DetectExplicitPlatform(query);
        var found = await identity.FindAccountsAsync(query, cancellationToken);
        var candidates = found.Accounts
            .Where(x => explicitPlatform is null ||
                        string.Equals(x.Platform, explicitPlatform, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0 && explicitPlatform is null && found.Accounts.Count == 1)
            candidates = found.Accounts.ToList();

        if (candidates.Count == 0)
            return (null, $"Аккаунт «{query}» не найден. Пользователь должен сначала написать боту.");

        if (candidates.Count > 1)
        {
            var variants = string.Join(", ", candidates.Select(x =>
                $"{Platforms.DisplayName(x.Platform)}: @{x.Username} ({x.DisplayName})"));
            return (null, $"Найдено несколько аккаунтов: {variants}. Укажите платформу или ID.");
        }

        return (candidates[0], null);
    }

    private async Task<(AccountDto? Account, string? Error)> ResolveSingleAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var found = await identity.FindAccountsAsync(query, cancellationToken);
        if (found.Accounts.Count == 0)
            return (null, $"Пользователь «{query}» не найден. Укажите @username из базы, ссылку или ID.");

        if (found.Accounts.Count > 1)
        {
            var variants = string.Join(", ", found.Accounts.Select(x =>
                $"{Platforms.DisplayName(x.Platform)}: @{x.Username} ({x.DisplayName})"));
            return (null, $"Найдено несколько аккаунтов: {variants}. Уточните платформу или укажите ссылку/ID.");
        }

        return (found.Accounts[0], null);
    }

    private async Task<bool> IsAdminAsync(Message message, CancellationToken cancellationToken)
    {
        if (message.From is null)
            return false;

        if (_options.AdminUserIds.Any(x =>
                string.Equals(x, message.From.Id.ToString(), StringComparison.Ordinal)))
            return true;

        if (message.Chat.Type is not (ChatType.Group or ChatType.Supergroup))
            return false;

        try
        {
            var member = await bot.GetChatMember(
                message.Chat.Id,
                message.From.Id,
                cancellationToken);

            return member is ChatMemberOwner or ChatMemberAdministrator;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Не удалось проверить права администратора Telegram {UserId}", message.From.Id);
            return false;
        }
    }

    private Task ReplyAsync(Message message, string text, CancellationToken cancellationToken)
        => sender.TrySendTextAsync(message.Chat.Id, text, cancellationToken: cancellationToken);

    private static (string Command, string Args) SplitCommand(string text)
    {
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith('/'))
            return (string.Empty, string.Empty);

        var spaceIndex = trimmed.IndexOf(' ');
        var command = spaceIndex < 0 ? trimmed : trimmed[..spaceIndex];
        var args = spaceIndex < 0 ? string.Empty : trimmed[(spaceIndex + 1)..].Trim();

        var atIndex = command.IndexOf('@');
        if (atIndex > 0)
            command = command[..atIndex];

        return (command.ToLowerInvariant(), args);
    }

    private static List<string> SplitArgs(string args)
        => args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();

    private static bool LooksLikeReference(string token)
        => token.Length > 0 &&
           (ReferencePrefixes.Any(prefix => token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
            token.All(char.IsDigit));

    private static (List<string> Tokens, bool Personal, bool CurrentOnly) ParseFlags(string args)
    {
        var tokens = SplitArgs(args);
        var personal = false;
        var currentOnly = true;

        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            var token = tokens[i];
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var parts = token[2..].Split('=', 2);
                var name = parts[0];
                var value = parts.Length > 1 ? parts[1] : null;

                switch (name.ToLowerInvariant())
                {
                    case "pm":
                    case "personal":
                        personal = value is null || !bool.TryParse(value, out var pm) || pm;
                        tokens.RemoveAt(i);
                        continue;
                    case "cross":
                        currentOnly = value is not null && bool.TryParse(value, out var cross) && cross;
                        tokens.RemoveAt(i);
                        continue;
                    case "only":
                    case "current-only":
                        currentOnly = value is null || !bool.TryParse(value, out var only) || only;
                        tokens.RemoveAt(i);
                        continue;
                }
            }
        }

        // Позиционные флаги в конце: <текст> [personal] [current-only].
        if (tokens.Count >= 3 &&
            bool.TryParse(tokens[^2], out var positionalPersonal) &&
            bool.TryParse(tokens[^1], out var positionalOnly) &&
            IsValidFlagSplit(tokens, 2))
        {
            personal = positionalPersonal;
            currentOnly = positionalOnly;
            tokens.RemoveRange(tokens.Count - 2, 2);
        }
        else if (tokens.Count >= 2 &&
                 bool.TryParse(tokens[^1], out var positionalPersonalOnly) &&
                 IsValidFlagSplit(tokens, 1))
        {
            personal = positionalPersonalOnly;
            tokens.RemoveAt(tokens.Count - 1);
        }

        return (tokens, personal, currentOnly);
    }

    private static bool IsValidFlagSplit(List<string> tokens, int flagsToRemove)
    {
        var remaining = tokens.Count - flagsToRemove;
        if (remaining < 1)
            return false;

        // Если остаётся только ссылка на получателя, это не флаг, а сам текст.
        return remaining >= 2 || !LooksLikeReference(tokens[0]);
    }
}
