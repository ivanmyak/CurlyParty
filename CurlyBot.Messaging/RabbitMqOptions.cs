using CurlyBot.Contracts.Messaging;

namespace CurlyBot.Messaging;

/// <summary>
///     Настройки подключения к RabbitMQ и параметров обмена сообщениями.
///     Заполняются из секции конфигурации <c>RabbitMq</c>.
/// </summary>
public sealed class RabbitMqOptions
{
    /// <summary>Имя или адрес хоста RabbitMQ.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>Порт AMQP-подключения.</summary>
    public int Port { get; set; } = 5672;

    /// <summary>Имя пользователя для аутентификации в RabbitMQ.</summary>
    public string UserName { get; set; } = "guest";

    /// <summary>Пароль для аутентификации в RabbitMQ.</summary>
    public string Password { get; set; } = "guest";

    /// <summary>Виртуальный хост RabbitMQ.</summary>
    public string VirtualHost { get; set; } = "/";

    /// <summary>Основной exchange, в который публикуются интеграционные события.</summary>
    public string Exchange { get; set; } = RabbitTopology.Exchange;

    /// <summary>Максимальное количество неподтверждённых сообщений, выдаваемых consumer'у за раз.</summary>
    public ushort PrefetchCount { get; set; } = 10;

    /// <summary>Задержка перед повторным подключением consumer'а при сбое, в секундах.</summary>
    public int ReconnectDelaySeconds { get; set; } = 5;
}
