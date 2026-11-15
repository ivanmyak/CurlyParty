using Discord;
using Discord.Commands;
using Discord.Interactions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CurlyBot.DiscordBot.Commands
{
    /// <summary>Слеш-команда вывода списка доступных команд.</summary>
    public class HelpCommands : InteractionModuleBase<SocketInteractionContext>
    {
        private readonly InteractionService _interactions;

        /// <summary>Создаёт модуль справки.</summary>
        /// <param name="interactions">Служба взаимодействий Discord для перечисления команд.</param>
        public HelpCommands(InteractionService interactions)
        {
            _interactions = interactions;
        }

        /// <summary>Отправляет список всех зарегистрированных slash-команд.</summary>
        /// <returns>Задача выполнения команды.</returns>
        [SlashCommand("help", "Выводит список всех доступных slash-команд.")]
        public async Task HelpAsync()
        {
            var embedBuilder = new EmbedBuilder()
                .WithTitle("?? Список команд")
                .WithColor(Color.DarkBlue)
                .WithDescription("Все доступные команды бота:");

            foreach (var cmd in _interactions.Modules.SelectMany(m => m.SlashCommands))
            {
                string summary = cmd.Description ?? "Нет описания";
                embedBuilder.AddField($"/{cmd.Name}", summary, inline: false);
            }

            await RespondAsync(embed: embedBuilder.Build(), ephemeral: true);
        }
    }
}
