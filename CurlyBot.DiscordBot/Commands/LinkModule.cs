using CurlyBot.Clients;
using CurlyBot.Contracts;
using CurlyBot.Contracts.Identity;
using CurlyBot.DiscordBot.Services;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace CurlyBot.DiscordBot.Commands
{
    /// <summary>Слеш-команды управления связками аккаунтов Discord и Telegram.</summary>
    public class LinkModule : InteractionModuleBase<SocketInteractionContext>
    {
        private readonly IdentityServiceClient _identity;
        private readonly ILogger<LoggingService> _logger;

        /// <summary>Создаёт модуль команд связки аккаунтов.</summary>
        /// <param name="identity">Клиент IdentityService.</param>
        /// <param name="logger">Журнал бота.</param>
        public LinkModule(IdentityServiceClient identity, ILogger<LoggingService> logger)
        {
            _identity = identity;
            _logger = logger;
        }

        /// <summary>Создаёт запрос на связку текущего Discord-аккаунта с аккаунтом Telegram.</summary>
        /// <param name="target">Имя пользователя, ссылка на профиль или идентификатор в Telegram.</param>
        /// <returns>Задача выполнения команды.</returns>
        [SlashCommand("linkme", "Связать ваш Discord-аккаунт с аккаунтом в Telegram")]
        public async Task LinkMeAsync(
            [Summary("target", "@username, ссылка на профиль или ID аккаунта в Telegram")] string target)
        {
            var caller = Context.User as SocketGuildUser;
            if (caller == null)
            {
                await RespondAsync("Команда доступна только на сервере.", ephemeral: true);
                return;
            }

            try
            {
                await _identity.UpsertAccountAsync(
                    Platforms.Discord,
                    caller.Id.ToString(),
                    caller.Username,
                    caller.DisplayName);

                var targetPlatform = AccountQuery.ResolveLinkTargetPlatform(target, Platforms.Discord);
                var result = await _identity.CreateLinkRequestAsync(
                    Platforms.Discord,
                    caller.Id.ToString(),
                    targetPlatform,
                    target);

                await RespondAsync(result.Message, ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[LINK] Ошибка /linkme для {User}", caller.Id);
                await RespondAsync("Сервис связки аккаунтов временно недоступен.", ephemeral: true);
            }
        }

        /// <summary>Связывает указанного пользователя с аккаунтом другого сервиса (только администратор).</summary>
        /// <param name="user">Discord-пользователь.</param>
        /// <param name="target">Имя пользователя, ссылка на профиль или идентификатор в Telegram.</param>
        /// <returns>Задача выполнения команды.</returns>
        [SlashCommand("linkuser", "Связать Discord-пользователя с аккаунтом в Telegram (администратор)")]
        [RequireUserPermission(GuildPermission.Administrator)]
        public async Task LinkUserAsync(
            [Summary("user", "Discord-пользователь")] SocketGuildUser user,
            [Summary("target", "@username, ссылка на профиль или ID аккаунта в Telegram")] string target)
        {
            var caller = Context.User as SocketGuildUser;
            try
            {
                await _identity.UpsertAccountAsync(
                    Platforms.Discord,
                    user.Id.ToString(),
                    user.Username,
                    user.DisplayName);

                var targetPlatform = AccountQuery.ResolveLinkTargetPlatform(target, Platforms.Discord);
                var result = await _identity.AdminLinkAsync(
                    Platforms.Discord,
                    user.Id.ToString(),
                    targetPlatform,
                    target);

                _logger.LogInformation(
                    "[LINK] Администратор {Admin} связал {User} с {Target} ({Status})",
                    caller?.Id,
                    user.Id,
                    target,
                    result.Status);

                await RespondAsync(result.Message, ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[LINK] Ошибка /linkuser для {User}", user.Id);
                await RespondAsync("Сервис связки аккаунтов временно недоступен.", ephemeral: true);
            }
        }

        /// <summary>Разрывает связку текущего Discord-аккаунта.</summary>
        /// <returns>Задача выполнения команды.</returns>
        [SlashCommand("unlinkme", "Разорвать связку вашего аккаунта с другим сервисом")]
        public async Task UnlinkMeAsync()
        {
            var caller = Context.User as SocketGuildUser;
            if (caller == null)
            {
                await RespondAsync("Команда доступна только на сервере.", ephemeral: true);
                return;
            }

            try
            {
                var result = await _identity.UnlinkAsync(Platforms.Discord, caller.Id.ToString());
                await RespondAsync(result.Message, ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[LINK] Ошибка /unlinkme для {User}", caller.Id);
                await RespondAsync("Сервис связки аккаунтов временно недоступен.", ephemeral: true);
            }
        }

        /// <summary>Разрывает связку указанного пользователя (только администратор).</summary>
        /// <param name="user">Discord-пользователь.</param>
        /// <returns>Задача выполнения команды.</returns>
        [SlashCommand("unlinkuser", "Разорвать связку Discord-пользователя (администратор)")]
        [RequireUserPermission(GuildPermission.Administrator)]
        public async Task UnlinkUserAsync(
            [Summary("user", "Discord-пользователь")] SocketGuildUser user)
        {
            try
            {
                var result = await _identity.UnlinkAsync(Platforms.Discord, user.Id.ToString());
                await RespondAsync(result.Message, ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[LINK] Ошибка /unlinkuser для {User}", user.Id);
                await RespondAsync("Сервис связки аккаунтов временно недоступен.", ephemeral: true);
            }
        }

        /// <summary>Показывает текущие связки аккаунтов.</summary>
        /// <param name="user">Пользователь для просмотра; доступно администратору.</param>
        /// <returns>Задача выполнения команды.</returns>
        [SlashCommand("linkstatus", "Показать текущие связки аккаунтов")]
        public async Task LinkStatusAsync(
            [Summary("user", "Пользователь (только для администраторов)")] SocketGuildUser? user = null)
        {
            var caller = Context.User as SocketGuildUser;
            if (caller == null)
            {
                await RespondAsync("Команда доступна только на сервере.", ephemeral: true);
                return;
            }

            if (user != null && user.Id != caller.Id && !caller.GuildPermissions.Administrator)
            {
                await RespondAsync("Просматривать чужие связки может только администратор.", ephemeral: true);
                return;
            }

            var target = user ?? caller;

            try
            {
                await _identity.UpsertAccountAsync(
                    Platforms.Discord,
                    target.Id.ToString(),
                    target.Username,
                    target.DisplayName);

                var profile = await _identity.GetProfileAsync(Platforms.Discord, target.Id.ToString());
                if (profile == null)
                {
                    await RespondAsync("Профиль не найден в базе.", ephemeral: true);
                    return;
                }

                if (profile.LinkedAccounts.Count == 0)
                {
                    await RespondAsync(
                        $"**{target.DisplayName}**: связанных аккаунтов нет.\n" +
                        "Используйте `/linkme <@username или ссылка>`, чтобы создать связку.",
                        ephemeral: true);
                    return;
                }

                var own = profile.Account;
                var lines = new List<string>
                {
                    $"- {Platforms.DisplayName(own.Platform)}: **{own.DisplayName}** (@{own.Username}) — текущий аккаунт" +
                    (own.DmBlocked ? " — ЛС закрыты" : string.Empty)
                };
                lines.AddRange(profile.LinkedAccounts.Select(x =>
                    $"- {Platforms.DisplayName(x.Platform)}: **{x.DisplayName}** (@{x.Username})" +
                    (x.DmBlocked ? " — ЛС закрыты" : string.Empty)));

                await RespondAsync(
                    $"**Связки {target.DisplayName}:**\n{string.Join("\n", lines)}",
                    ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[LINK] Ошибка /linkstatus для {User}", target.Id);
                await RespondAsync("Сервис связки аккаунтов временно недоступен.", ephemeral: true);
            }
        }
    }
}
