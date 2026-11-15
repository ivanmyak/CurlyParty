using CurlyBot.Clients;
using CurlyBot.Contracts;
using CurlyBot.Contracts.Identity;
using CurlyBot.DiscordBot.Interfaces;
using CurlyBot.DiscordBot.Services;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CurlyBot.DiscordBot.Commands
{
    /// <summary>Слеш-команда озвучивания текста с доставкой в Discord и связанные аккаунты.</summary>
    public class TalkModule : InteractionModuleBase<SocketInteractionContext>
    {
        private readonly IVoiceQueue _voiceQueue;
        private readonly IVoiceTemplate _templates;
        private readonly IdentityServiceClient _identity;
        private readonly SpeechSynthesisClient _speech;
        private readonly CrossPlatformDeliveryClient _delivery;
        private readonly ILogger<LoggingService> _logger;
        private readonly int _maxTextLength;

        /// <summary>Создаёт модуль команды озвучивания.</summary>
        /// <param name="config">Конфигурация приложения.</param>
        /// <param name="voiceQueue">Очередь озвучки голосовых каналов и личных сообщений.</param>
        /// <param name="templates">Сервис форматирования текста перед озвучкой.</param>
        /// <param name="identity">Клиент IdentityService.</param>
        /// <param name="speech">Клиент синтеза речи.</param>
        /// <param name="delivery">Клиент кросс-платформенной доставки.</param>
        /// <param name="logger">Журнал бота.</param>
        public TalkModule(
            IConfiguration config,
            IVoiceQueue voiceQueue,
            IVoiceTemplate templates,
            IdentityServiceClient identity,
            SpeechSynthesisClient speech,
            CrossPlatformDeliveryClient delivery,
            ILogger<LoggingService> logger)
        {
            _voiceQueue = voiceQueue;
            _templates = templates;
            _identity = identity;
            _speech = speech;
            _delivery = delivery;
            _logger = logger;

            var maxTextLengthStr = config["VoiceSettings:MaxTextLength"];

            if (!int.TryParse(maxTextLengthStr, out _maxTextLength) || _maxTextLength <= 0)
            {
                _maxTextLength = 300;
                _logger.LogWarning("[TALK] VoiceSettings:MaxTextLength отсутствует или некорректен. Используем значение по умолчанию: 300");
            }
            else
            {
                _logger.LogInformation("[TALK] MaxTextLength загружен из конфигурации: {Value}", _maxTextLength);
            }
        }

        /// <summary>Озвучивает текст и доставляет его получателю.</summary>
        /// <param name="text">Текст для озвучки.</param>
        /// <param name="user">Получатель: Discord-пользователь, @username из базы или Telegram-аккаунт.</param>
        /// <param name="isPersonalMessage">Отправить сразу в личные сообщения вместо голосового канала.</param>
        /// <param name="isInCurrentServiceOnly">Ограничить доставку только текущим сервисом.</param>
        /// <returns>Задача выполнения команды.</returns>
        [SlashCommand("talk", "Озвучить текст голосом бота")]
        public async Task TalkAsync(
            [Summary("text", "Текст для озвучки")] string text,
            [Summary("user", "Discord-пользователь, @username из базы или @telegram-аккаунт (необязательно)")] string? user = null,
            [Summary("is-personal-message", "Отправить сразу в личку (true) или пытаться в голосовой (false)")] bool isPersonalMessage = false,
            [Summary("is-in-current-service-only", "true — только Discord; false — дополнительно связанному аккаунту в Telegram")] bool isInCurrentServiceOnly = true)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                await RespondAsync("Текст пустой.", ephemeral: true);
                return;
            }

            if (text.Length > _maxTextLength)
            {
                await RespondAsync(
                    $"Текст слишком длинный (максимум {_maxTextLength} символов, а в сообщении {text.Length} символов).",
                    ephemeral: true);
                return;
            }

            var caller = Context.User as SocketGuildUser;
            if (caller == null)
            {
                await RespondAsync("Не удалось определить пользователя.", ephemeral: true);
                return;
            }

            AccountDto callerAccount;
            try
            {
                callerAccount = await _identity.UpsertAccountAsync(
                    Platforms.Discord,
                    caller.Id.ToString(),
                    caller.Username,
                    caller.DisplayName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TALK] IdentityService недоступен при регистрации пользователя {User}", caller.Id);
                await RespondAsync("Сервис пользователей временно недоступен. Попробуйте позже.", ephemeral: true);
                return;
            }

            var (target, profile, resolveError) = await ResolveTargetAsync(user, caller, callerAccount);
            if (target == null || profile == null)
            {
                await RespondAsync(resolveError ?? "Не удалось определить получателя.", ephemeral: true);
                return;
            }

            var formatted = _templates.FormatTalk(text);
            var isSelf = target.Platform == Platforms.Discord &&
                         target.ExternalId == caller.Id.ToString();

            _logger.LogInformation(
                "[TALK] Caller={Caller}({CallerId}), Target={TargetPlatform}/{TargetId} ({TargetName}), PM={PM}, CurrentOnly={CurrentOnly}, Text=\"{Text}\"",
                caller.DisplayName, caller.Id,
                target.Platform, target.ExternalId, target.DisplayName,
                isPersonalMessage, isInCurrentServiceOnly, text);

            if (target.Platform == Platforms.Telegram)
            {
                await HandleTelegramTargetAsync(
                    caller,
                    target,
                    profile,
                    formatted,
                    isPersonalMessage,
                    isInCurrentServiceOnly,
                    isSelf);
                return;
            }

            await HandleDiscordTargetAsync(
                caller,
                target,
                profile,
                formatted,
                isPersonalMessage,
                isInCurrentServiceOnly,
                isSelf);
        }

        private async Task HandleDiscordTargetAsync(
            SocketGuildUser caller,
            AccountDto target,
            ProfileDto profile,
            string formatted,
            bool isPersonalMessage,
            bool isInCurrentServiceOnly,
            bool isSelf)
        {
            if (!ulong.TryParse(target.ExternalId, out var targetId))
            {
                await RespondAsync("Некорректный Discord ID получателя.", ephemeral: true);
                return;
            }

            SocketUser? recipientUser = isSelf
                ? caller
                : Context.Guild.GetUser(targetId)
                  ?? await Context.Client.GetUserAsync(targetId, new RequestOptions { RetryMode = RetryMode.AlwaysRetry }) as SocketUser;

            if (recipientUser == null)
            {
                await RespondAsync(
                    "Пользователь найден в базе, но Discord не вернул его профиль. Попробуйте позже.",
                    ephemeral: true);
                return;
            }

            var recipientGuildUser = recipientUser as SocketGuildUser;
            var willSpeakInVoice = !isPersonalMessage && recipientGuildUser?.VoiceChannel != null;

            await _voiceQueue.EnqueueDirectMessageAsync(
                caller,
                formatted,
                isPersonalMessage,
                recipientUser);

            var messages = new List<string>();

            if (willSpeakInVoice)
            {
                messages.Add(isSelf
                    ? "Добавлено в очередь озвучки в вашем голосовом канале."
                    : $"Добавлено в очередь озвучки в голосовом канале пользователя **{target.DisplayName}**.");
            }
            else
            {
                messages.Add(isSelf
                    ? "Озвучка будет отправлена вам в личные сообщения."
                    : $"Озвучка поставлена в очередь на отправку пользователю **{target.DisplayName}** в личные сообщения.");
            }

            if (!isInCurrentServiceOnly)
            {
                var telegram = profile.LinkedAccounts.FirstOrDefault(x => x.Platform == Platforms.Telegram);
                if (telegram == null)
                {
                    messages.Add("Связанный Telegram-аккаунт не найден.");
                }
                else
                {
                    var notification = isSelf
                        ? "Вы отправили сообщение самому себе:"
                        : $"Игрок {caller.DisplayName} сказал вам:";
                    var delivered = await TryDeliverToAccountAsync(telegram, formatted, notification, caller);
                    messages.Add(delivered
                        ? $"Копия отправлена связанному аккаунту **{telegram.DisplayName}** в Telegram."
                        : "Не удалось отправить копию в Telegram (см. логи).");
                }
            }

            await RespondAsync(string.Join("\n", messages), ephemeral: true);
        }

        private async Task HandleTelegramTargetAsync(
            SocketGuildUser caller,
            AccountDto target,
            ProfileDto profile,
            string formatted,
            bool isPersonalMessage,
            bool isInCurrentServiceOnly,
            bool isSelf)
        {
            var notification = isSelf
                ? "Вы отправили сообщение самому себе:"
                : $"Игрок {caller.DisplayName} сказал вам:";

            var delivered = await TryDeliverToAccountAsync(target, formatted, notification, caller);
            if (!delivered)
            {
                await RespondAsync(
                    "Не удалось отправить озвучку в Telegram. Проверьте, что пользователь не заблокировал бота.",
                    ephemeral: true);
                return;
            }

            var messages = new List<string>
            {
                $"Озвучка отправлена пользователю **{target.DisplayName}** в Telegram."
            };

            if (!isInCurrentServiceOnly)
            {
                var discord = profile.LinkedAccounts.FirstOrDefault(x => x.Platform == Platforms.Discord);
                if (discord != null)
                {
                    var copyDelivered = await TryDeliverToAccountAsync(discord, formatted, notification, caller);
                    messages.Add(copyDelivered
                        ? $"Копия отправлена связанному Discord-аккаунту **{discord.DisplayName}**."
                        : "Не удалось отправить копию в Discord (см. логи).");
                }
            }

            await RespondAsync(string.Join("\n", messages), ephemeral: true);
        }

        private async Task<bool> TryDeliverToAccountAsync(
            AccountDto target,
            string formatted,
            string notification,
            SocketGuildUser caller)
        {
            try
            {
                var speech = await _speech.SynthesizeAsync(formatted);
                await _delivery.DeliverAsync(
                    target.Platform,
                    target.ExternalId,
                    target.DisplayName,
                    caller.DisplayName,
                    notification,
                    speech);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[TALK] Не удалось доставить озвучку в {Platform} пользователю {TargetId}",
                    target.Platform,
                    target.ExternalId);
                return false;
            }
        }

        private async Task<(AccountDto? Target, ProfileDto? Profile, string? Error)> ResolveTargetAsync(
            string? query,
            SocketGuildUser caller,
            AccountDto callerAccount)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                var selfProfile = await _identity.GetProfileAsync(
                    callerAccount.Platform,
                    callerAccount.ExternalId);

                return (callerAccount, selfProfile ?? new ProfileDto(callerAccount, Array.Empty<AccountDto>()), null);
            }

            FindAccountsResponse found;
            try
            {
                found = await _identity.FindAccountsAsync(query);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TALK] Ошибка поиска пользователя по запросу {Query}", query);
                return (null, null, "Сервис пользователей временно недоступен. Попробуйте позже.");
            }

            if (found.Accounts.Count == 0)
            {
                var fallback = await ResolveGuildMemberAsync(query);
                if (fallback.Error != null)
                    return (null, null, fallback.Error);

                if (fallback.Account != null)
                {
                    var fallbackProfile = await _identity.GetProfileAsync(
                        fallback.Account.Platform,
                        fallback.Account.ExternalId);

                    return (fallback.Account, fallbackProfile ?? new ProfileDto(fallback.Account, Array.Empty<AccountDto>()), null);
                }

                return (null, null,
                    $"Пользователь «{query}» не найден. Укажите @username из базы, упоминание Discord или @telegram-аккаунт.");
            }

            if (found.Accounts.Count > 1)
            {
                var variants = string.Join(", ", found.Accounts.Select(x =>
                    $"{Platforms.DisplayName(x.Platform)}: @{x.Username} ({x.DisplayName})"));
                return (null, null, $"Найдено несколько аккаунтов: {variants}. Уточните платформу или укажите ID/ссылку.");
            }

            var target = found.Accounts[0];
            var profile = await _identity.GetProfileAsync(target.Platform, target.ExternalId)
                          ?? new ProfileDto(target, Array.Empty<AccountDto>());

            return (target, profile, null);
        }

        private async Task<(AccountDto? Account, string? Error)> ResolveGuildMemberAsync(string query)
        {
            var normalized = AccountQuery.NormalizeUsername(query);

            var matches = Context.Guild.Users
                .Where(x =>
                    string.Equals(x.Username.ToLowerInvariant(), normalized, StringComparison.Ordinal) ||
                    string.Equals(x.DisplayName.ToLowerInvariant(), normalized, StringComparison.Ordinal))
                .ToList();

            if (matches.Count == 0)
                return (null, null);

            if (matches.Count > 1)
            {
                var variants = string.Join(", ", matches.Select(x => $"{x.DisplayName} ({x.Username})"));
                return (null, $"Найдено несколько участников сервера: {variants}. Укажите @username точнее.");
            }

            var member = matches[0];
            var account = await _identity.UpsertAccountAsync(
                Platforms.Discord,
                member.Id.ToString(),
                member.Username,
                member.DisplayName);

            return (account, null);
        }
    }
}
