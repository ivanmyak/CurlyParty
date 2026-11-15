using CurlyBot.Contracts.Messaging;
using CurlyBot.Messaging;

namespace CurlyBot.Clients;

/// <summary>Регистрация клиентов CurlyBot в DI-контейнере.</summary>
public static class ClientServiceCollectionExtensions
{
    /// <summary>Добавляет HTTP-клиент IdentityService.</summary>
    /// <param name="services">Коллекция сервисов.</param>
    /// <param name="configuration">Конфигурация приложения.</param>
    /// <returns>Та же коллекция сервисов для цепочки вызовов.</returns>
    public static IServiceCollection AddIdentityServiceClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<IdentityServiceOptions>(configuration.GetSection("IdentityService"));
        services.AddHttpClient<IdentityServiceClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<IdentityServiceOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        return services;
    }

    /// <summary>
    ///     Добавляет клиент синтеза речи и клиент кросс-платформенной доставки.
    /// </summary>
    /// <param name="services">Коллекция сервисов.</param>
    /// <param name="configuration">Конфигурация приложения.</param>
    /// <param name="replyQueue">Очередь, в которую TTS-сервис вернёт результат.</param>
    /// <returns>Та же коллекция сервисов для цепочки вызовов.</returns>
    public static IServiceCollection AddSpeechSynthesisClient(
        this IServiceCollection services,
        IConfiguration configuration,
        string replyQueue)
    {
        services.Configure<SpeechClientOptions>(configuration.GetSection("SpeechClient"));
        services.PostConfigure<SpeechClientOptions>(options => options.ReplyQueue = replyQueue);

        services.AddSingleton<SpeechSynthesisClient>();
        services.AddSingleton<CrossPlatformDeliveryClient>();

        services.AddRabbitMqConsumer<SpeechCompleted>(
            replyQueue,
            replyQueue,
            sp => sp.GetRequiredService<SpeechSynthesisClient>());

        return services;
    }
}

/// <summary>Публикует готовую озвучку боту другой платформы.</summary>
public sealed class CrossPlatformDeliveryClient(IIntegrationEventPublisher publisher)
{
    /// <summary>Отправляет событие доставки озвучки в сервис целевой платформы.</summary>
    /// <param name="targetPlatform">Платформа получателя.</param>
    /// <param name="targetExternalId">Идентификатор получателя на его платформе.</param>
    /// <param name="targetDisplayName">Отображаемое имя получателя.</param>
    /// <param name="sourceDisplayName">Отображаемое имя отправителя.</param>
    /// <param name="notificationText">Текст сопроводительного сообщения.</param>
    /// <param name="speech">Результат синтеза речи.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача публикации события.</returns>
    public Task DeliverAsync(
        string targetPlatform,
        string targetExternalId,
        string targetDisplayName,
        string? sourceDisplayName,
        string? notificationText,
        SpeechSynthesisResult speech,
        CancellationToken cancellationToken = default)
    {
        var delivery = new SpeechDeliveryRequested(
            Guid.NewGuid(),
            targetPlatform,
            targetExternalId,
            targetDisplayName,
            sourceDisplayName,
            notificationText,
            speech.WavPath,
            speech.OggPath);

        return publisher.PublishAsync(
            RabbitTopology.DeliveryRoutingKey(targetPlatform),
            delivery,
            cancellationToken);
    }
}
