using System.Text.Json;
using CurlyBot.Contracts.Messaging;
using RabbitMQ.Client.Events;

namespace CurlyBot.Messaging;

/// <summary>
///     Универсальный consumer: объявляет exchange/очередь/binding, читает JSON,
///     вызывает scoped-обработчик и подтверждает сообщение.
///     При ошибке сообщение уходит в DLQ, чтобы не терять данные и не блокировать очередь.
/// </summary>
/// <typeparam name="TEvent">Тип обрабатываемого события.</typeparam>
public sealed class RabbitMqConsumerService<TEvent> : BackgroundService
    where TEvent : class
{
    private readonly IRabbitMqConnection _connection;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqConsumerService<TEvent>> _logger;
    private readonly string _queueName;
    private readonly string _routingKey;

    /// <summary>Создаёт consumer для указанной очереди и routing key.</summary>
    /// <param name="connection">Подключение к RabbitMQ.</param>
    /// <param name="scopeFactory">Фабрика scoped-контейнеров для обработчиков.</param>
    /// <param name="options">Настройки RabbitMQ.</param>
    /// <param name="logger">Журнал consumer'а.</param>
    /// <param name="queueName">Имя очереди.</param>
    /// <param name="routingKey">Ключ маршрутизации.</param>
    public RabbitMqConsumerService(
        IRabbitMqConnection connection,
        IServiceScopeFactory scopeFactory,
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqConsumerService<TEvent>> logger,
        string queueName,
        string routingKey)
    {
        _connection = connection;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
        _queueName = queueName;
        _routingKey = routingKey;
    }

    /// <summary>Запускает цикл чтения очереди с переподключением при сбоях.</summary>
    /// <param name="stoppingToken">Токен остановки службы.</param>
    /// <returns>Задача работы consumer'а до остановки приложения.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "RabbitMQ consumer {Queue} упал. Переподключение через {Delay} сек.",
                    _queueName,
                    _options.ReconnectDelaySeconds);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_options.ReconnectDelaySeconds), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        await using var channel = await _connection.CreateChannelAsync(cancellationToken);

        await DeclareTopologyAsync(channel, cancellationToken);

        await channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: _options.PrefetchCount,
            global: false,
            cancellationToken: cancellationToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, args) =>
        {
            try
            {
                var integrationEvent = JsonSerializer.Deserialize<TEvent>(
                    args.Body.Span,
                    IntegrationEventJson.Options);

                if (integrationEvent is null)
                    throw new InvalidOperationException($"Не удалось разобрать {typeof(TEvent).Name}");

                using var scope = _scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<IIntegrationEventHandler<TEvent>>();

                await handler.HandleAsync(integrationEvent, cancellationToken);
                await channel.BasicAckAsync(args.DeliveryTag, multiple: false, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Ошибка обработки {EventType} из очереди {Queue}. Сообщение будет отправлено в DLQ.",
                    typeof(TEvent).Name,
                    _queueName);

                try
                {
                    await channel.BasicNackAsync(
                        deliveryTag: args.DeliveryTag,
                        multiple: false,
                        requeue: false,
                        cancellationToken: cancellationToken);
                }
                catch (Exception nackEx)
                {
                    _logger.LogWarning(nackEx, "Не удалось отправить сообщение в DLQ");
                }
            }
        };

        await channel.BasicConsumeAsync(
            queue: _queueName,
            autoAck: false,
            consumer: consumer,
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "RabbitMQ consumer запущен: queue={Queue}, exchange={Exchange}, routingKey={RoutingKey}",
            _queueName,
            _options.Exchange,
            _routingKey);

        // Держим consumer живым, пока канал открыт. Если RabbitMQ перезапустился —
        // выходим из метода и переподключаемся во внешнем цикле.
        while (channel.IsOpen && !cancellationToken.IsCancellationRequested)
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    }

    private async Task DeclareTopologyAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(
            _options.Exchange,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null,
            passive: false,
            noWait: false,
            cancellationToken: cancellationToken);

        await channel.ExchangeDeclareAsync(
            RabbitTopology.DeadLetterExchange,
            ExchangeType.Fanout,
            durable: true,
            autoDelete: false,
            arguments: null,
            passive: false,
            noWait: false,
            cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            RabbitTopology.DeadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            passive: false,
            noWait: false,
            cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            RabbitTopology.DeadLetterQueue,
            RabbitTopology.DeadLetterExchange,
            routingKey: string.Empty,
            arguments: null,
            noWait: false,
            cancellationToken: cancellationToken);

        var queueArguments = new Dictionary<string, object?>
        {
            ["x-dead-letter-exchange"] = RabbitTopology.DeadLetterExchange
        };

        await channel.QueueDeclareAsync(
            _queueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: queueArguments,
            passive: false,
            noWait: false,
            cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            _queueName,
            _options.Exchange,
            _routingKey,
            arguments: null,
            noWait: false,
            cancellationToken: cancellationToken);
    }
}
