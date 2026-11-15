using System.Text.RegularExpressions;

namespace CurlyBot.Contracts.Identity;

/// <summary>
///     Тип значения в разобранном поисковом запросе: внешний идентификатор или никнейм.
/// </summary>
public enum AccountQueryKind
{
    /// <summary>Запрос содержит точный внешний идентификатор аккаунта (например, числовой id).</summary>
    ExternalId,

    /// <summary>Запрос содержит никнейм, который нужно искать без учёта регистра.</summary>
    Username
}

/// <summary>
///     Результат разбора пользовательского поискового запроса.
/// </summary>
/// <param name="Platform">Канонический идентификатор платформы, к которой относится запрос.</param>
/// <param name="Kind">Тип найденного значения — идентификатор или никнейм.</param>
/// <param name="Value">Нормализованное значение для поиска.</param>
/// <param name="PlatformExplicit">Признак того, что платформу явно указал пользователь.</param>
public sealed record ParsedAccountQuery(
    string Platform,
    AccountQueryKind Kind,
    string Value,
    bool PlatformExplicit);

/// <summary>
///     Разбор пользовательского ввода: @username, упоминание Discord, ссылка на профиль,
///     числовой id, tg://user?id=... и т.п.
///     Понимает и «человеческие» никнеймы, и прямые ссылки/идентификаторы.
/// </summary>
public static partial class AccountQuery
{
    [GeneratedRegex(@"^<@!?(\d+)>$", RegexOptions.Compiled)]
    private static partial Regex DiscordMentionRegex();

    [GeneratedRegex(@"^(?:https?://)?(?:www\.)?discord(?:app)?\.com/users/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DiscordProfileUrlRegex();

    [GeneratedRegex(@"^tg://user\?id=(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TelegramDeepLinkRegex();

    [GeneratedRegex(@"^(?:https?://)?(?:www\.)?(?:t|telegram)\.me/([A-Za-z0-9_]{3,})", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TelegramProfileUrlRegex();

    [GeneratedRegex(@"^\d+$", RegexOptions.Compiled)]
    private static partial Regex DigitsRegex();

    /// <summary>
    ///     Приводит никнейм к виду, пригодному для поиска: убирает пробелы по краям,
    ///     ведущий символ «@», переводит в нижний регистр и заменяет «ё» на «е»,
    ///     чтобы «Пётр» находился по запросу «петр».
    /// </summary>
    /// <param name="value">Исходный никнейм; допускает null.</param>
    /// <returns>Нормализованный никнейм в нижнем регистре.</returns>
    public static string NormalizeUsername(string? value)
    {
        var result = (value ?? string.Empty).Trim();
        if (result.StartsWith('@'))
            result = result[1..];
        return result.ToLowerInvariant().Replace('ё', 'е');
    }

    /// <summary>
    ///     Разбирает поисковый запрос с учётом явного префикса платформы, упоминаний,
    ///     ссылок на профиль и числовых идентификаторов.
    /// </summary>
    /// <param name="platform">Платформа по умолчанию, если она не указана в запросе.</param>
    /// <param name="rawQuery">Пользовательский ввод в свободной форме.</param>
    /// <returns>Разобранный запрос с нормализованным значением и признаком явной платформы.</returns>
    public static ParsedAccountQuery Parse(string platform, string rawQuery)
    {
        platform = Platforms.Normalize(platform);
        var raw = (rawQuery ?? string.Empty).Trim();
        if (raw.Length == 0)
            return new ParsedAccountQuery(platform, AccountQueryKind.Username, string.Empty, false);

        var explicitPlatform = false;
        if (TryStripPrefix(ref raw, "discord:", "ds:"))
        {
            platform = Platforms.Discord;
            explicitPlatform = true;
        }
        else if (TryStripPrefix(ref raw, "telegram:", "tg:"))
        {
            platform = Platforms.Telegram;
            explicitPlatform = true;
        }

        if (platform == Platforms.Discord)
        {
            var mention = DiscordMentionRegex().Match(raw);
            if (mention.Success)
                return new ParsedAccountQuery(platform, AccountQueryKind.ExternalId, mention.Groups[1].Value, explicitPlatform);

            var profile = DiscordProfileUrlRegex().Match(raw);
            if (profile.Success)
                return new ParsedAccountQuery(platform, AccountQueryKind.ExternalId, profile.Groups[1].Value, explicitPlatform);

            if (DigitsRegex().IsMatch(raw))
                return new ParsedAccountQuery(platform, AccountQueryKind.ExternalId, raw, explicitPlatform);

            return new ParsedAccountQuery(
                platform,
                AccountQueryKind.Username,
                NormalizeUsername(raw),
                explicitPlatform);
        }

        var telegramDeepLink = TelegramDeepLinkRegex().Match(raw);
        if (telegramDeepLink.Success)
            return new ParsedAccountQuery(platform, AccountQueryKind.ExternalId, telegramDeepLink.Groups[1].Value, explicitPlatform);

        var telegramProfile = TelegramProfileUrlRegex().Match(raw);
        if (telegramProfile.Success)
            return new ParsedAccountQuery(platform, AccountQueryKind.Username, NormalizeUsername(telegramProfile.Groups[1].Value), explicitPlatform);

        if (DigitsRegex().IsMatch(raw))
            return new ParsedAccountQuery(platform, AccountQueryKind.ExternalId, raw, explicitPlatform);

        return new ParsedAccountQuery(
            platform,
            AccountQueryKind.Username,
            NormalizeUsername(raw),
            explicitPlatform);
    }

    /// <summary>
    ///     Определяет платформу, которую явно указал пользователь.
    ///     Если явного указания нет, возвращает null.
    /// </summary>
    /// <param name="rawQuery">Пользовательский ввод в свободной форме.</param>
    /// <returns>Идентификатор явно указанной платформы либо null.</returns>
    public static string? DetectExplicitPlatform(string rawQuery)
    {
        var raw = (rawQuery ?? string.Empty).Trim();
        if (raw.Length == 0)
            return null;

        if (raw.StartsWith("discord:", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("ds:", StringComparison.OrdinalIgnoreCase) ||
            DiscordMentionRegex().IsMatch(raw) ||
            DiscordProfileUrlRegex().IsMatch(raw))
            return Platforms.Discord;

        if (raw.StartsWith("telegram:", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("tg:", StringComparison.OrdinalIgnoreCase) ||
            TelegramDeepLinkRegex().IsMatch(raw) ||
            TelegramProfileUrlRegex().IsMatch(raw))
            return Platforms.Telegram;

        return null;
    }

    /// <summary>
    ///     Для /linkme: если платформа не указана явно, считаем, что пользователь
    ///     хочет связаться с аккаунтом на другой платформе.
    /// </summary>
    /// <param name="rawQuery">Пользовательский ввод с платформой или без неё.</param>
    /// <param name="sourcePlatform">Платформа, с которой выполняется связывание.</param>
    /// <returns>Идентификатор платформы, на которой нужно искать целевой аккаунт.</returns>
    public static string ResolveLinkTargetPlatform(string rawQuery, string sourcePlatform)
        => DetectExplicitPlatform(rawQuery) ?? Platforms.Other(sourcePlatform);

    /// <summary>
    ///     Для /talk: ищем по обеим платформам, если явного указания нет.
    /// </summary>
    /// <param name="rawQuery">Пользовательский ввод с платформой или без неё.</param>
    /// <returns>Список платформ, которые нужно проверить при поиске аккаунта.</returns>
    public static IReadOnlyList<string> ResolveSearchPlatforms(string rawQuery)
        => DetectExplicitPlatform(rawQuery) is { } platform
            ? new[] { platform }
            : new[] { Platforms.Discord, Platforms.Telegram };

    private static bool TryStripPrefix(ref string value, params string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            value = value[prefix.Length..].Trim();
            return true;
        }

        return false;
    }
}
