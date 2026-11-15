namespace CurlyBot.Messaging;

/// <summary>
///     Обработчик интеграционного события, получаемого из RabbitMQ.
/// </summary>
/// <typeparam name="TEvent">Тип обрабатываемого события.</typeparam>
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : class
{
    /// <summary>Обрабатывает одно интеграционное событие.</summary>
    /// <param name="integrationEvent">Данные события.</param>
    /// <param name="cancellationToken">Токен отмены обработки.</param>
    /// <returns>Задача обработки события.</returns>
    Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken);
}
