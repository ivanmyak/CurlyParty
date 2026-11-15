using CurlyBot.Contracts.Models;

namespace CurlyBot.Contracts.Commands;

/// <summary>
///     Команда озвучки, общая для Discord и Telegram.
/// </summary>
public sealed record TalkCommand
{
    /// <summary>Пользователь, отправивший команду озвучки.</summary>
    public PlatformUser Sender { get; init; } = null!;

    /// <summary>Получатель сообщения; null, если озвучка предназначена для голосового канала отправителя.</summary>
    public PlatformUser? Recipient { get; init; }

    /// <summary>Текст, который нужно синтезировать и озвучить.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Отправлять голосовое сообщение в личку, а не пытаться говорить в голосовом канале.</summary>
    public bool IsPersonalMessage { get; init; }

    /// <summary>
    ///     true  — работаем только в текущем сервисе (Discord или Telegram);
    ///     false — дополнительно отправляем связанному аккаунту во втором сервисе.
    /// </summary>
    public bool IsInCurrentServiceOnly { get; init; } = true;
}
