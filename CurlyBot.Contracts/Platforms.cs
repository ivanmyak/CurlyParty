namespace CurlyBot.Contracts;

/// <summary>
///     Идентификаторы платформ, которые используются в базе и интеграционных событиях.
///     Всегда храним их в нижнем регистре, чтобы не зависеть от регистра в конфигурации.
/// </summary>
public static class Platforms
{
    /// <summary>Идентификатор платформы Discord.</summary>
    public const string Discord = "discord";

    /// <summary>Идентификатор платформы Telegram.</summary>
    public const string Telegram = "telegram";

    /// <summary>
    ///     Приводит произвольное название платформы к каноническому идентификатору.
    ///     Понимает короткие алиасы (ds/tg) и русские названия, нечувствителен к регистру
    ///     и пробелам по краям. Неизвестное значение возвращается в нижнем регистре.
    /// </summary>
    /// <param name="platform">Название или идентификатор платформы; допускает null.</param>
    /// <returns>Канонический идентификатор платформы либо нормализованное исходное значение.</returns>
    public static string Normalize(string? platform)
        => (platform ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "discord" or "ds" or "дискорд" => Discord,
            "telegram" or "tg" or "телеграм" or "телеграмм" => Telegram,
            var other => other
        };

    /// <summary>
    ///     Возвращает «вторую» платформу относительно указанной: для Discord — Telegram,
    ///     для любой другой — Discord.
    /// </summary>
    /// <param name="platform">Исходная платформа.</param>
    /// <returns>Идентификатор парной платформы.</returns>
    public static string Other(string platform)
        => Normalize(platform) == Discord ? Telegram : Discord;

    /// <summary>
    ///     Возвращает человекочитаемое название платформы для сообщений пользователю.
    ///     Для неизвестной платформы возвращает исходную строку.
    /// </summary>
    /// <param name="platform">Название или идентификатор платформы.</param>
    /// <returns>Название платформы с правильным регистром.</returns>
    public static string DisplayName(string platform)
        => Normalize(platform) switch
        {
            Discord => "Discord",
            Telegram => "Telegram",
            _ => platform
        };
}
