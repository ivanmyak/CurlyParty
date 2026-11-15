using CurlyBot.Clients;
using CurlyBot.Contracts;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace CurlyBot.DiscordBot.Services
{
    /// <summary>Служба журналирования событий Discord и регистрации пользователей в IdentityService.</summary>
    public class LoggingService
    {
        private readonly ILogger<LoggingService> _logger;
        private readonly DiscordSocketClient _client;
        private readonly IdentityServiceClient _identity;

        /// <summary>Подписывается на события Discord и регистрирует службу.</summary>
        /// <param name="logger">Журнал службы.</param>
        /// <param name="client">Клиент Discord.</param>
        /// <param name="identity">Клиент IdentityService.</param>
        public LoggingService(
            ILogger<LoggingService> logger,
            DiscordSocketClient client,
            IdentityServiceClient identity)
        {
            _logger = logger;
            _client = client;
            _identity = identity;

            _client.MessageReceived += OnBotMessageReceivedAsync;
            _client.InteractionCreated += OnInteractionCreatedAsync;
            _client.UserJoined += OnUserJoinedAsync;
            _client.UserLeft += OnUserLeftAsync;
            _client.PresenceUpdated += OnPresenceUpdatedAsync;
            _logger.LogInformation("Зарегистрировали Лог-сервис!");
        }

        private async Task OnBotMessageReceivedAsync(SocketMessage message)
        {
            if (message.Channel is SocketDMChannel && !message.Author.IsBot)
            {
                _logger.LogInformation($"[BotMessage] {message.Author.GlobalName}({message.Author.Username}) написал боту в личку: {message.Content}");
            }

            if (!message.Author.IsBot)
                await UpsertSafeAsync(message.Author);
        }

        private async Task OnInteractionCreatedAsync(SocketInteraction interaction)
        {
            string commandInfo = interaction switch
            {
                SocketSlashCommand slash => $"/{slash.CommandName}",
                SocketMessageComponent component => $"Component {component.Data.CustomId}",
                SocketUserCommand userCmd => $"UserCommand {userCmd.CommandName}",
                SocketMessageCommand msgCmd => $"MessageCommand {msgCmd.CommandName}",
                _ => "Не удалось определить...."
            };

            _logger.LogInformation($"[Interaction] {interaction.User.GlobalName}({interaction.User.Username}) вызвал {commandInfo} в #{interaction.Channel?.Name}");
            await UpsertSafeAsync(interaction.User);
        }

        private async Task OnUserJoinedAsync(SocketGuildUser user)
        {
            _logger.LogInformation($"[Join] {user.DisplayName}({user.Username}) присоединился к серверу {user.Guild.Name}");
            await UpsertSafeAsync(user);
        }

        private Task OnUserLeftAsync(SocketGuild guild, SocketUser user)
        {
            _logger.LogWarning($"[Leave] {user.GlobalName}({user.Username}) покинул сервер {guild.Name}");
            return Task.CompletedTask;
        }

        private Task OnPresenceUpdatedAsync(SocketUser user, SocketPresence before, SocketPresence after)
        {
            var beforeActivities = before?.Activities ?? Enumerable.Empty<IActivity>();
            var afterActivities = after?.Activities ?? Enumerable.Empty<IActivity>();

            var beforeActivity = beforeActivities.FirstOrDefault();
            var afterActivity = afterActivities.FirstOrDefault();

            if (beforeActivity?.Name != afterActivity?.Name)
            {
                if (afterActivity != null)
                    _logger.LogInformation($"[Activity] {user.GlobalName}({user.Username}) начал {afterActivity.Name}");
                else if (beforeActivity != null)
                    _logger.LogInformation($"[Activity] {user.GlobalName}({user.Username}) завершил {beforeActivity.Name}");
            }
            else
            {
                _logger.LogInformation($"[Activity] У {user.GlobalName}({user.Username}) не удалось оценить состояние прошлой:{beforeActivity?.Name} и будущей: {afterActivity?.Name}");
            }

            return Task.CompletedTask;
        }

        private async Task UpsertSafeAsync(IUser user)
        {
            try
            {
                var displayName = (user as SocketGuildUser)?.DisplayName ?? user.GlobalName ?? user.Username;

                await _identity.UpsertAccountAsync(
                    Platforms.Discord,
                    user.Id.ToString(),
                    user.Username,
                    displayName);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Не удалось зарегистрировать пользователя {UserId} в IdentityService", user.Id);
            }
        }
    }
}
