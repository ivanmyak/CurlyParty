using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using CurlyBot.DiscordBot.Services;
using Microsoft.Extensions.Logging;

/// <summary>Слеш-команды управления списком игнорирования авто-перемещений.</summary>
public class IgnoreCommands : InteractionModuleBase<SocketInteractionContext>
{
    private readonly ILogger<LoggingService> _logger;

    /// <summary>Создаёт модуль команд игнорирования.</summary>
    /// <param name="logger">Журнал бота.</param>
    public IgnoreCommands(ILogger<LoggingService> logger)
    {
        _logger = logger;
    }

    /// <summary>Отключает авто-перемещения для себя или указанного пользователя.</summary>
    /// <param name="target">Пользователь, которого нужно исключить; доступно администратору.</param>
    /// <returns>Задача выполнения команды.</returns>
    [SlashCommand("ignore", "Отключает авто-перемещения для текущего пользователя или указанного (только для админов).")]
    public async Task IgnoreAsync(SocketGuildUser? target = null)
    {
        var caller = Context.User as SocketGuildUser ?? throw new Exception("[IGNORE] Не удалось определить отправителя"); 
        var user = target ?? caller;

        // Проверка прав: если указан чужой пользователь, то только админ может
        if (target != null && !caller.GuildPermissions.Administrator)
        {
            await RespondAsync("У вас нет прав исключать других пользователей.", ephemeral: true);
            return;
        }

        IgnoreManager.SetTracking(user.Id, false);
        await RespondAsync($"{user.DisplayName}({user.Username}) теперь не будет отслеживаться ботом.", ephemeral: true);

        // Уведомление выбранному пользователю
        if (target != null)
        {
            try
            {
                await user.SendMessageAsync(
                    $"Администратор {caller.DisplayName}({caller.Username}) сервера {caller.Guild.Name} добавил Вас в список игнорирования бота по перемещениям.");
                _logger.LogInformation($"[Ignore] {caller.DisplayName}({caller.Username}) отключил отслеживание для {user.DisplayName}({user.Username}) на сервере {caller.Guild.Name}");
            }
            catch
            {
                await FollowupAsync($"Не удалось отправить уведомление пользователю {user.DisplayName}({user.Username}) (закрыты ЛС).", ephemeral: true);
            }
        }
    }

    /// <summary>Включает авто-перемещения для себя или указанного пользователя.</summary>
    /// <param name="target">Пользователь, которого нужно вернуть; доступно администратору.</param>
    /// <returns>Задача выполнения команды.</returns>
    [SlashCommand("unignore", "Включает авто-перемещения для текущего пользователя или указанного (только для админов).")]
    public async Task UnignoreAsync(SocketGuildUser? target = null)
    {
        SocketGuildUser caller = Context.User as SocketGuildUser ?? throw new Exception("[UNIGNORE] Не удалось определить отправителя");
        var user = target ?? caller;

        if (target != null && !caller.GuildPermissions.Administrator)
        {
            await RespondAsync("У вас нет прав включать других пользователей.", ephemeral: true);
            return;
        }

        IgnoreManager.SetTracking(user.Id, true);
        await RespondAsync($"{user.DisplayName}({user.Username}) снова отслеживается ботом.", ephemeral: true);

        if (target != null)
        {
            try
            {
                await user.SendMessageAsync(
                    $"Администратор {caller.DisplayName}({caller.Username}) сервера {caller.Guild.Name} исключил Вас из списка игнорирования бота по перемещениям.");
                _logger.LogInformation($"[Unignore] {caller.DisplayName}({caller.Username}) выключил отслеживание для {user.DisplayName}({user.Username}) на сервере {caller.Guild.Name}");
            }
            catch
            {
                await FollowupAsync($"Не удалось отправить уведомление пользователю {user.DisplayName}({user.Username}) (закрыты ЛС).", ephemeral: true);
            }
        }
    }

    /// <summary>Выводит список игнорируемых пользователей (только администратор).</summary>
    /// <returns>Задача выполнения команды.</returns>
    [SlashCommand("listignored", "Выводит список всех пользователей, отключивших авто-перемещения.")]
    [RequireUserPermission(GuildPermission.Administrator)]
    public async Task ListIgnoredAsync()
    {
        var ignored = IgnoreManager.GetIgnoredUsers().ToList();

        if (!ignored.Any())
        {
            await RespondAsync("Нет пользователей, отключивших авто-перемещения.", ephemeral: true);
            return;
        }

        string list = string.Join("\n", ignored.Select(id =>
        {
            var user = Context.Guild.GetUser(id);
            return user != null ? $"- {user.DisplayName}({user.Username})" : $"- {id}";
        }));

        await RespondAsync($"**Игнорируемые пользователи:**\n{list}", ephemeral: true);
    }
}
