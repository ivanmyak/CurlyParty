namespace CurlyBot.Clients;

/// <summary>Настройки HTTP-клиента IdentityService.</summary>
public sealed class IdentityServiceOptions
{
    /// <summary>Базовый адрес сервиса идентификации.</summary>
    public string BaseUrl { get; set; } = "http://localhost:8081";

    /// <summary>Таймаут HTTP-запросов в секундах.</summary>
    public int TimeoutSeconds { get; set; } = 15;
}

/// <summary>Настройки клиента синтеза речи через RabbitMQ.</summary>
public sealed class SpeechClientOptions
{
    /// <summary>Routing key/queue, куда TTS-сервис вернёт результат.</summary>
    public string ReplyQueue { get; set; } = string.Empty;

    /// <summary>Максимальное время ожидания ответа TTS-сервиса в секундах.</summary>
    public int TimeoutSeconds { get; set; } = 180;
}
