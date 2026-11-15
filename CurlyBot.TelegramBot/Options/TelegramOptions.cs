namespace CurlyBot.TelegramBot.Options;

/// <summary>Настройки Telegram-бота из секции конфигурации <c>Telegram</c>.</summary>
public sealed class TelegramOptions
{
    /// <summary>Токен бота, полученный у BotFather.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Числовые Telegram id пользователей, которым разрешены админ-команды.</summary>
    public string[] AdminUserIds { get; set; } = Array.Empty<string>();

    /// <summary>Максимальная длина текста для одного запроса озвучки.</summary>
    public int MaxTextLength { get; set; } = 550;

    /// <summary>Таймаут long polling в секундах.</summary>
    public int PollingTimeoutSeconds { get; set; } = 30;
}
