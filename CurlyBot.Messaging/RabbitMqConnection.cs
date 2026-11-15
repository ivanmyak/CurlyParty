using RabbitMQ.Client;

namespace CurlyBot.Messaging;

/// <summary>Поставщик каналов RabbitMQ.</summary>
public interface IRabbitMqConnection : IAsyncDisposable
{
    /// <summary>Создаёт новый канал AMQP.</summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Открытый канал RabbitMQ.</returns>
    Task<IChannel> CreateChannelAsync(CancellationToken cancellationToken = default);
}

/// <summary>Разделяемое подключение к RabbitMQ с автоматическим переподключением.</summary>
public sealed class RabbitMqConnection : IRabbitMqConnection
{
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqConnection> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    /// <summary>Создаёт подключение по настройкам RabbitMQ.</summary>
    /// <param name="options">Настройки подключения.</param>
    /// <param name="logger">Журнал подключения.</param>
    public RabbitMqConnection(IOptions<RabbitMqOptions> options, ILogger<RabbitMqConnection> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Создаёт новый канал AMQP на общем подключении.</summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Открытый канал RabbitMQ.</returns>
    public async Task<IChannel> CreateChannelAsync(CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync(cancellationToken);
        return await connection.CreateChannelAsync(cancellationToken: cancellationToken);
    }

    private async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true })
            return _connection;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true })
                return _connection;

            if (_connection is not null)
            {
                try { await _connection.DisposeAsync(); }
                catch (Exception ex) { _logger.LogDebug(ex, "RabbitMQ: ошибка закрытия старого подключения"); }
                _connection = null;
            }

            var factory = new ConnectionFactory
            {
                HostName = _options.Host,
                Port = _options.Port,
                UserName = _options.UserName,
                Password = _options.Password,
                VirtualHost = _options.VirtualHost,
                AutomaticRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(_options.ReconnectDelaySeconds),
                ClientProvidedName = AppDomain.CurrentDomain.FriendlyName
            };

            _connection = await factory.CreateConnectionAsync(cancellationToken);
            _logger.LogInformation(
                "RabbitMQ: подключение установлено ({Host}:{Port}, vhost={VirtualHost})",
                _options.Host,
                _options.Port,
                _options.VirtualHost);

            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Асинхронно закрывает подключение к RabbitMQ.</summary>
    /// <returns>Задача освобождения ресурсов.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_connection is null)
            return;

        try { await _connection.DisposeAsync(); }
        catch (Exception ex) { _logger.LogDebug(ex, "RabbitMQ: ошибка при закрытии подключения"); }
        _connection = null;
    }
}
