using CurlyBot.Clients;
using CurlyBot.Contracts;
using CurlyBot.Contracts.Identity;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace CurlyBot.DiscordBot.Services;

/// <summary>
///     Автодополнение параметра <c>user</c> команды <c>/talk</c>.
///     Сначала предлагает участников текущего сервера (они находятся в кэше Discord),
///     затем сохранённые аккаунты из базы связок — Discord и Telegram.
///     Значение подсказки содержит префикс платформы, поэтому выбранный аккаунт
///     разрешается однозначно, даже если ники на платформах совпадают.
/// </summary>
/// <param name="identity">Клиент IdentityService для поиска сохранённых аккаунтов.</param>
/// <param name="logger">Журнал бота.</param>
public sealed class TalkAutocompleteHandler(
    IdentityServiceClient identity,
    ILogger<TalkAutocompleteHandler> logger) : AutocompleteHandler
{
    private const int MaxChoices = 25;
    private const int MaxGuildChoices = 10;
    private const int MaxLabelLength = 100;

    /// <summary>Формирует список подсказок для текущего значения параметра.</summary>
    /// <param name="context">Контекст взаимодействия, из которого берётся сервер.</param>
    /// <param name="autocompleteInteraction">Взаимодействие автодополнения с введённым текстом.</param>
    /// <param name="parameter">Описание параметра, для которого запрошены подсказки.</param>
    /// <param name="services">Провайдер служб приложения.</param>
    /// <returns>Набор подсказок; ошибки базы не прерывают работу команды.</returns>
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context,
        IAutocompleteInteraction autocompleteInteraction,
        IParameterInfo parameter,
        IServiceProvider services)
    {
        var query = (autocompleteInteraction.Data.Current.Value?.ToString() ?? string.Empty).Trim();
        var choices = new List<AutocompleteResult>(MaxChoices);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (context is SocketInteractionContext { Guild: { } guild })
        {
            var members = guild.Users
                .Where(x => !x.IsBot && Matches(x, query))
                .OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Take(MaxGuildChoices);

            foreach (var member in members)
            {
                choices.Add(new AutocompleteResult(
                    Truncate($"{member.DisplayName} (@{member.Username}) — сервер"),
                    $"discord:{member.Id}"));
                seen.Add($"{Platforms.Discord}:{member.Id}");
            }
        }

        try
        {
            foreach (var account in await identity.SuggestAccountsAsync(query, MaxChoices))
            {
                if (choices.Count >= MaxChoices)
                    break;

                if (!seen.Add($"{account.Platform}:{account.ExternalId}"))
                    continue;

                choices.Add(new AutocompleteResult(Describe(account), ValueFor(account)));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "[TALK] Не удалось получить подсказки из базы связок по запросу «{Query}»",
                query);
        }

        return AutocompletionResult.FromSuccess(choices);
    }

    private static bool Matches(IGuildUser user, string query)
        => query.Length == 0
           || user.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
           || user.Username.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string Describe(AccountDto account)
        => Truncate($"{account.DisplayName} (@{account.Username}) — {Platforms.DisplayName(account.Platform)}");

    private static string ValueFor(AccountDto account)
        => account.Platform == Platforms.Telegram
            ? $"tg:{account.Username}"
            : $"discord:{account.ExternalId}";

    private static string Truncate(string value)
        => value.Length <= MaxLabelLength ? value : value[..MaxLabelLength];
}
