using System.Collections.Concurrent;
using Telegram.Bot.Types;

namespace CurlyBot.TelegramBot.Services;

/// <summary>Шаг пошагового сценария заполнения параметров команды <c>/talk</c>.</summary>
public enum TalkDialogStep
{
    /// <summary>Просмотр меню параметров.</summary>
    Menu,

    /// <summary>Ожидание текста озвучки.</summary>
    Text,

    /// <summary>Ожидание получателя озвучки.</summary>
    Target
}

/// <summary>Черновик команды <c>/talk</c>, заполняемый пользователем по шагам.</summary>
public sealed class TalkDraft
{
    /// <summary>Идентификатор чата, в котором начат сценарий.</summary>
    public long ChatId { get; init; }

    /// <summary>Идентификатор пользователя, начавшего сценарий.</summary>
    public long UserId { get; init; }

    /// <summary>Идентификатор сообщения с меню параметров.</summary>
    public int MenuMessageId { get; set; }

    /// <summary>Исходное сообщение с командой <c>/talk</c>.</summary>
    public Message CommandMessage { get; init; } = null!;

    /// <summary>Текущий шаг сценария.</summary>
    public TalkDialogStep Step { get; set; } = TalkDialogStep.Menu;

    /// <summary>Введённый текст озвучки.</summary>
    public string? Text { get; set; }

    /// <summary>Сырая ссылка на получателя или <see langword="null"/>, если озвучка себе.</summary>
    public string? TargetQuery { get; set; }

    /// <summary>Отображаемое имя выбранного получателя для меню.</summary>
    public string? TargetName { get; set; }

    /// <summary>Признак личного сообщения получателю.</summary>
    public bool Personal { get; set; }

    /// <summary>Признак отправки только в текущий сервис.</summary>
    public bool CurrentOnly { get; set; } = true;

    /// <summary>Подсказка или ошибка, показываемая на текущем шаге.</summary>
    public string? Hint { get; set; }

    /// <summary>Момент, после которого черновик считается просроченным.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
///     Хранит незавершённые сценарии <c>/talk</c> в памяти процесса. Ключ включает чат и
///     пользователя, чтобы в группах черновики разных людей не пересекались.
/// </summary>
public sealed class TalkDialogState
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<TalkDialogKey, TalkDraft> _drafts = new();

    /// <summary>Возвращает актуальный черновик пользователя или <see langword="null"/>.</summary>
    /// <param name="chatId">Идентификатор чата.</param>
    /// <param name="userId">Идентификатор пользователя.</param>
    /// <returns>Черновик либо <see langword="null"/>, если сценария нет или он просрочен.</returns>
    public TalkDraft? Get(long chatId, long userId)
    {
        var key = new TalkDialogKey(chatId, userId);
        if (!_drafts.TryGetValue(key, out var draft))
            return null;

        if (draft.ExpiresAt > DateTimeOffset.UtcNow)
            return draft;

        _drafts.TryRemove(key, out _);
        return null;
    }

    /// <summary>Создаёт или заменяет черновик пользователя.</summary>
    /// <param name="chatId">Идентификатор чата.</param>
    /// <param name="userId">Идентификатор пользователя.</param>
    /// <param name="commandMessage">Сообщение с командой <c>/talk</c>.</param>
    /// <returns>Созданный черновик.</returns>
    public TalkDraft Start(long chatId, long userId, Message commandMessage)
    {
        RemoveExpired();

        var draft = new TalkDraft
        {
            ChatId = chatId,
            UserId = userId,
            CommandMessage = commandMessage,
            ExpiresAt = DateTimeOffset.UtcNow.Add(Lifetime)
        };

        _drafts[new TalkDialogKey(chatId, userId)] = draft;
        return draft;
    }

    /// <summary>Продлевает срок жизни черновика после действия пользователя.</summary>
    /// <param name="draft">Черновик сценария.</param>
    public void Touch(TalkDraft draft)
        => draft.ExpiresAt = DateTimeOffset.UtcNow.Add(Lifetime);

    /// <summary>Удаляет черновик пользователя.</summary>
    /// <param name="chatId">Идентификатор чата.</param>
    /// <param name="userId">Идентификатор пользователя.</param>
    public void Remove(long chatId, long userId)
        => _drafts.TryRemove(new TalkDialogKey(chatId, userId), out _);

    private void RemoveExpired()
    {
        if (_drafts.IsEmpty)
            return;

        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _drafts)
        {
            if (pair.Value.ExpiresAt <= now)
                _drafts.TryRemove(pair.Key, out _);
        }
    }

    private readonly record struct TalkDialogKey(long ChatId, long UserId);
}
