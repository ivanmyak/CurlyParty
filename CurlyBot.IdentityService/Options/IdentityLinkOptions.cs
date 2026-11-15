namespace CurlyBot.IdentityService.Options;

/// <summary>Настройки сроков жизни запросов на связку и периода ожидания после отказа.</summary>
public sealed class IdentityLinkOptions
{
    /// <summary>Срок жизни запроса на связку в минутах.</summary>
    public int RequestTtlMinutes { get; set; } = 60 * 24;

    /// <summary>Период ожидания перед повторной попыткой после отказа, в минутах.</summary>
    public int DeclineCooldownMinutes { get; set; } = 30;
}
