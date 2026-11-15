namespace CurlyBot.Contracts.Models;

/// <summary>
///     Платформо-независимое представление пользователя.
///     Используется командами и интеграционными событиями, чтобы Discord- и Telegram-адаптеры
///     могли говорить на одном языке.
/// </summary>
/// <param name="Platform">Имя платформы: <c>discord</c> или <c>telegram</c>.</param>
/// <param name="UserId">Идентификатор пользователя внутри платформы.</param>
/// <param name="DisplayName">Отображаемое имя пользователя.</param>
/// <param name="Username">Имя пользователя (логин), если оно известно.</param>
public sealed record PlatformUser(
    string Platform,
    string UserId,
    string DisplayName,
    string? Username = null)
{
    /// <summary>Признак того, что пользователь относится к Discord.</summary>
    public bool IsDiscord => Platforms.Normalize(Platform) == Platforms.Discord;

    /// <summary>Признак того, что пользователь относится к Telegram.</summary>
    public bool IsTelegram => Platforms.Normalize(Platform) == Platforms.Telegram;

    /// <summary>Создаёт описание Discord-пользователя.</summary>
    /// <param name="userId">Идентификатор пользователя Discord.</param>
    /// <param name="displayName">Отображаемое имя.</param>
    /// <param name="username">Имя пользователя Discord.</param>
    /// <returns>Экземпляр <see cref="PlatformUser"/> для Discord.</returns>
    public static PlatformUser Discord(string userId, string displayName, string? username = null)
        => new(Platforms.Discord, userId, displayName, username);

    /// <summary>Создаёт описание Telegram-пользователя.</summary>
    /// <param name="userId">Идентификатор пользователя Telegram.</param>
    /// <param name="displayName">Отображаемое имя.</param>
    /// <param name="username">Имя пользователя Telegram.</param>
    /// <returns>Экземпляр <see cref="PlatformUser"/> для Telegram.</returns>
    public static PlatformUser Telegram(string userId, string displayName, string? username = null)
        => new(Platforms.Telegram, userId, displayName, username);
}
