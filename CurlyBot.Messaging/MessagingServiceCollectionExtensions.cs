using CurlyBot.Contracts.Messaging;

namespace CurlyBot.Messaging;

/// <summary>Регистрация компонентов RabbitMQ в DI-контейнере.</summary>
public static class MessagingServiceCollectionExtensions
{
    /// <summary>Добавляет подключение к RabbitMQ и публикатор интеграционных событий.</summary>
    /// <param name="services">Коллекция сервисов.</param>
    /// <param name="configuration">Конфигурация приложения.</param>
    /// <returns>Та же коллекция сервисов для цепочки вызовов.</returns>
    public static IServiceCollection AddRabbitMqMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<RabbitMqOptions>(configuration.GetSection("RabbitMq"));
        services.AddSingleton<IRabbitMqConnection, RabbitMqConnection>();
        services.AddSingleton<IIntegrationEventPublisher, RabbitMqEventPublisher>();
        return services;
    }

    /// <summary>Добавляет consumer, который создаёт обработчик через DI.</summary>
    /// <typeparam name="TEvent">Тип обрабатываемого события.</typeparam>
    /// <typeparam name="THandler">Тип обработчика события.</typeparam>
    /// <param name="services">Коллекция сервисов.</param>
    /// <param name="queueName">Имя очереди.</param>
    /// <param name="routingKey">Ключ маршрутизации.</param>
    /// <returns>Та же коллекция сервисов для цепочки вызовов.</returns>
    public static IServiceCollection AddRabbitMqConsumer<TEvent, THandler>(
        this IServiceCollection services,
        string queueName,
        string routingKey)
        where TEvent : class
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        services.AddScoped<IIntegrationEventHandler<TEvent>, THandler>();
        return services.AddRabbitMqConsumer<TEvent>(queueName, routingKey);
    }

    /// <summary>Добавляет consumer события с собственным именем очереди и routing key.</summary>
    /// <typeparam name="TEvent">Тип обрабатываемого события.</typeparam>
    /// <param name="services">Коллекция сервисов.</param>
    /// <param name="queueName">Имя очереди.</param>
    /// <param name="routingKey">Ключ маршрутизации.</param>
    /// <param name="handlerFactory">Необязательная фабрика обработчика; если задана, обработчик регистрируется как singleton.</param>
    /// <returns>Та же коллекция сервисов для цепочки вызовов.</returns>
    public static IServiceCollection AddRabbitMqConsumer<TEvent>(
        this IServiceCollection services,
        string queueName,
        string routingKey,
        Func<IServiceProvider, IIntegrationEventHandler<TEvent>>? handlerFactory = null)
        where TEvent : class
    {
        if (handlerFactory is not null)
            services.AddSingleton<IIntegrationEventHandler<TEvent>>(handlerFactory);

        services.AddHostedService(sp => ActivatorUtilities.CreateInstance<RabbitMqConsumerService<TEvent>>(
            sp,
            queueName,
            routingKey));

        return services;
    }
}
