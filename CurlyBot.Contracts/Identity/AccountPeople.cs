namespace CurlyBot.Contracts.Identity;

/// <summary>
///     Группирует аккаунты по «человеку»: записи с одинаковым
///     <see cref="AccountDto.LinkGroupId"/> считаются одним получателем, даже если
///     они находятся на разных платформах.
/// </summary>
public static class AccountPeople
{
    /// <summary>Разбивает найденные аккаунты на людей с учётом связок.</summary>
    /// <param name="accounts">Аккаунты в порядке выдачи поиска.</param>
    /// <returns>
    ///     Список людей; каждый элемент — один или несколько связанных аккаунтов.
    ///     Несвязанные аккаунты всегда образуют отдельного человека.
    /// </returns>
    public static IReadOnlyList<IReadOnlyList<AccountDto>> Group(IEnumerable<AccountDto> accounts)
    {
        var groups = new List<List<AccountDto>>();
        var indexByLinkGroup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var account in accounts)
        {
            if (string.IsNullOrWhiteSpace(account.LinkGroupId))
            {
                groups.Add(new List<AccountDto> { account });
                continue;
            }

            if (indexByLinkGroup.TryGetValue(account.LinkGroupId, out var index))
            {
                groups[index].Add(account);
                continue;
            }

            indexByLinkGroup[account.LinkGroupId] = groups.Count;
            groups.Add(new List<AccountDto> { account });
        }

        return groups
            .Select(group => (IReadOnlyList<AccountDto>)group)
            .ToArray();
    }

    /// <summary>Выбирает аккаунт, на который нужно доставить сообщение.</summary>
    /// <param name="person">Аккаунты одного человека.</param>
    /// <param name="preferredPlatform">
    ///     Платформа, с которой пришла команда: её аккаунт предпочтителен, чтобы ответ
    ///     ушёл в текущий сервис, а копия на другую платформу управлялась отдельным флагом.
    /// </param>
    /// <returns>Аккаунт текущей платформы, если он есть, иначе первый доступный.</returns>
    public static AccountDto Representative(IReadOnlyList<AccountDto> person, string? preferredPlatform)
    {
        if (!string.IsNullOrWhiteSpace(preferredPlatform))
        {
            var preferred = person.FirstOrDefault(x =>
                string.Equals(x.Platform, Platforms.Normalize(preferredPlatform), StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
                return preferred;
        }

        return person.FirstOrDefault(x => !x.DmBlocked) ?? person[0];
    }

    /// <summary>Описывает человека для сообщения об ошибке или списка выбора.</summary>
    /// <param name="person">Аккаунты одного человека.</param>
    /// <param name="preferredPlatform">Платформа, чьё имя предпочтительно показать.</param>
    /// <returns>Имя, ник и перечень платформ, например «Иван (@ivan) — Telegram + Discord».</returns>
    public static string Describe(IReadOnlyList<AccountDto> person, string? preferredPlatform = null)
    {
        var representative = Representative(person, preferredPlatform);
        var platforms = string.Join(
            " + ",
            person
                .Select(x => Platforms.DisplayName(x.Platform))
                .Distinct(StringComparer.OrdinalIgnoreCase));

        return $"{representative.DisplayName} (@{representative.Username}) — {platforms}";
    }
}
