namespace CurlyBot.Contracts.Messaging;

/// <summary>
///     Имена exchange, очередей и routing key. Одно место правды для всех сервисов.
/// </summary>
public static class RabbitTopology
{
    /// <summary>Основной topic-exchange для интеграционных событий.</summary>
    public const string Exchange = "curlybot.events";

    /// <summary>Exchange, в который попадают сообщения, не прошедшие обработку.</summary>
    public const string DeadLetterExchange = "curlybot.dlx";

    /// <summary>Очередь для сообщений из dead-letter exchange.</summary>
    public const string DeadLetterQueue = "curlybot.dead";

    /// <summary>Routing key запроса на синтез речи.</summary>
    public const string SpeechRequestRoutingKey = "speech.request";

    /// <summary>Очередь запросов на синтез речи.</summary>
    public const string SpeechRequestsQueue = "curlybot.speech.requests";

    /// <summary>Очередь ответов TTS-сервиса для Discord-бота.</summary>
    public const string SpeechRepliesDiscordQueue = "curlybot.speech.replies.discord";

    /// <summary>Очередь ответов TTS-сервиса для Telegram-бота.</summary>
    public const string SpeechRepliesTelegramQueue = "curlybot.speech.replies.telegram";

    /// <summary>Префикс routing key запросов подтверждения связки.</summary>
    public const string LinkConfirmationRoutingKeyPrefix = "link.confirmation.";

    /// <summary>Префикс routing key результатов связки.</summary>
    public const string LinkResultRoutingKeyPrefix = "link.result.";

    /// <summary>Префикс routing key доставки готовой озвучки.</summary>
    public const string DeliveryRoutingKeyPrefix = "delivery.";

    /// <summary>Возвращает routing key запроса подтверждения связки для платформы.</summary>
    /// <param name="platform">Имя платформы.</param>
    /// <returns>Routing key вида <c>link.confirmation.{platform}</c>.</returns>
    public static string LinkConfirmationRoutingKey(string platform)
        => LinkConfirmationRoutingKeyPrefix + Platforms.Normalize(platform);

    /// <summary>Возвращает имя очереди подтверждения связки для платформы.</summary>
    /// <param name="platform">Имя платформы.</param>
    /// <returns>Имя очереди вида <c>curlybot.link.confirmation.{platform}</c>.</returns>
    public static string LinkConfirmationQueue(string platform)
        => "curlybot.link.confirmation." + Platforms.Normalize(platform);

    /// <summary>Возвращает routing key результата связки для платформы.</summary>
    /// <param name="platform">Имя платформы.</param>
    /// <returns>Routing key вида <c>link.result.{platform}</c>.</returns>
    public static string LinkResultRoutingKey(string platform)
        => LinkResultRoutingKeyPrefix + Platforms.Normalize(platform);

    /// <summary>Возвращает имя очереди результатов связки для платформы.</summary>
    /// <param name="platform">Имя платформы.</param>
    /// <returns>Имя очереди вида <c>curlybot.link.result.{platform}</c>.</returns>
    public static string LinkResultQueue(string platform)
        => "curlybot.link.result." + Platforms.Normalize(platform);

    /// <summary>Возвращает routing key доставки озвучки для платформы.</summary>
    /// <param name="platform">Имя платформы.</param>
    /// <returns>Routing key вида <c>delivery.{platform}</c>.</returns>
    public static string DeliveryRoutingKey(string platform)
        => DeliveryRoutingKeyPrefix + Platforms.Normalize(platform);

    /// <summary>Возвращает имя очереди доставки озвучки для платформы.</summary>
    /// <param name="platform">Имя платформы.</param>
    /// <returns>Имя очереди вида <c>curlybot.delivery.{platform}</c>.</returns>
    public static string DeliveryQueue(string platform)
        => "curlybot.delivery." + Platforms.Normalize(platform);
}
