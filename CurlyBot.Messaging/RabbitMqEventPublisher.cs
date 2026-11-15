using System.Text.Json;
using CurlyBot.Contracts.Messaging;
using RabbitMQ.Client;

namespace CurlyBot.Messaging;

/// <summary>Публикатор интеграционных событий в RabbitMQ.</summary>
public interface IIntegrationEventPublisher
{
    /// <summary>Публикует событие в exchange с указанным routing key.</summary>
    /// <typeparam name="TEvent">Тип публикуемого события.</typeparam>
    /// <param name="routingKey">Ключ маршрутизации.</param>
    /// <param name="integrationEvent">Данные события.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача публикации.</returns>
    Task PublishAsync<TEvent>(string routingKey, TEvent integrationEvent, CancellationToken cancellationToken = default)
        where TEvent : class;
}

/// <summary>Публикует события в topic-exchange RabbitMQ в формате JSON.</summary>
public sealed class RabbitMqEventPublisher : IIntegrationEventPublisher
{
    private readonly IRabbitMqConnection _connection;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqEventPublisher> _logger;

    /// <summary>Создаёт публикатор событий.</summary>
    /// <param name="connection">Подключение к RabbitMQ.</param>
    /// <param name="options">Настройки RabbitMQ.</param>
    /// <param name="logger">Журнал публикатора.</param>
    public RabbitMqEventPublisher(
        IRabbitMqConnection connection,
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqEventPublisher> logger)
    {
        _connection = connection;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Публикует событие в настроенный topic-exchange.</summary>
    /// <typeparam name="TEvent">Тип публикуемого события.</typeparam>
    /// <param name="routingKey">Ключ маршрутизации.</param>
    /// <param name="integrationEvent">Данные события.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача публикации.</returns>
    public async Task PublishAsync<TEvent>(
        string routingKey,
        TEvent integrationEvent,
        CancellationToken cancellationToken = default)
        where TEvent : class
    {
        await using var channel = await _connection.CreateChannelAsync(cancellationToken);

        await channel.ExchangeDeclareAsync(
            _options.Exchange,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null,
            passive: false,
            noWait: false,
            cancellationToken: cancellationToken);

        var body = JsonSerializer.SerializeToUtf8Bytes(integrationEvent, IntegrationEventJson.Options);

        var properties = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
            ContentEncoding = "utf-8",
            MessageId = Guid.NewGuid().ToString("N"),
            Type = typeof(TEvent).Name,
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        };

        await channel.BasicPublishAsync(
            exchange: _options.Exchange,
            routingKey: routingKey,
            mandatory: false,
            basicProperties: properties,
            body: body,
            cancellationToken: cancellationToken);

        _logger.LogDebug(
            "RabbitMQ: опубликовано {EventType} ({RoutingKey}), {Bytes} байт",
            typeof(TEvent).Name,
            routingKey,
            body.Length);
    }
}
