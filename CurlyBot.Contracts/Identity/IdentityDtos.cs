namespace CurlyBot.Contracts.Identity;

/// <summary>
///     Аккаунт пользователя на конкретной платформе.
///     LinkGroupId == null означает, что аккаунт ещё ни с кем не связан.
/// </summary>
/// <param name="Platform">Канонический идентификатор платформы (discord или telegram).</param>
/// <param name="ExternalId">Идентификатор пользователя на платформе (Discord id или Telegram id).</param>
/// <param name="Username">Никнейм на платформе в нормализованном виде.</param>
/// <param name="DisplayName">Отображаемое имя пользователя.</param>
/// <param name="LinkGroupId">Идентификатор группы связки аккаунтов или null, если аккаунт не связан.</param>
/// <param name="DmBlocked">Признак того, что пользователь запретил писать ему в личные сообщения.</param>
public sealed record AccountDto(
    string Platform,
    string ExternalId,
    string Username,
    string DisplayName,
    string? LinkGroupId,
    bool DmBlocked)
{
    /// <summary>Признак того, что аккаунт входит в группу связки.</summary>
    public bool IsLinked => !string.IsNullOrWhiteSpace(LinkGroupId);
}

/// <summary>
///     Профиль пользователя: сам аккаунт и все связанные с ним аккаунты на других платформах.
/// </summary>
/// <param name="Account">Основной аккаунт, для которого запрошен профиль.</param>
/// <param name="LinkedAccounts">Список связанных аккаунтов на других платформах.</param>
public sealed record ProfileDto(
    AccountDto Account,
    IReadOnlyList<AccountDto> LinkedAccounts);

/// <summary>
///     Запрос на создание или обновление аккаунта в хранилище идентичности.
/// </summary>
/// <param name="Platform">Платформа аккаунта.</param>
/// <param name="ExternalId">Идентификатор пользователя на платформе.</param>
/// <param name="Username">Никнейм пользователя на платформе.</param>
/// <param name="DisplayName">Отображаемое имя пользователя.</param>
public sealed record UpsertAccountRequest(
    string Platform,
    string ExternalId,
    string Username,
    string DisplayName);

/// <summary>
///     Запрос на отметку последнего контакта с пользователем.
/// </summary>
/// <param name="Platform">Платформа аккаунта.</param>
/// <param name="ExternalId">Идентификатор пользователя на платформе.</param>
public sealed record TouchContactRequest(
    string Platform,
    string ExternalId);

/// <summary>
///     Запрос на изменение запрета личных сообщений для аккаунта.
/// </summary>
/// <param name="Platform">Платформа аккаунта.</param>
/// <param name="ExternalId">Идентификатор пользователя на платформе.</param>
/// <param name="Blocked">true — личные сообщения запрещены, false — разрешены.</param>
public sealed record SetDmBlockedRequest(
    string Platform,
    string ExternalId,
    bool Blocked);

/// <summary>
///     Результат поиска аккаунтов по пользовательскому запросу.
/// </summary>
/// <param name="Accounts">Найденные аккаунты.</param>
/// <param name="Ambiguous">Признак неоднозначного результата, когда найдено несколько подходящих аккаунтов.</param>
public sealed record FindAccountsResponse(
    IReadOnlyList<AccountDto> Accounts,
    bool Ambiguous);

/// <summary>
///     Запрос на связывание аккаунтов от имени инициатора.
/// </summary>
/// <param name="SourcePlatform">Платформа аккаунта-инициатора.</param>
/// <param name="SourceExternalId">Идентификатор аккаунта-инициатора на платформе.</param>
/// <param name="TargetPlatform">Платформа целевого аккаунта.</param>
/// <param name="TargetQuery">Поисковый запрос для нахождения целевого аккаунта.</param>
public sealed record CreateLinkRequest(
    string SourcePlatform,
    string SourceExternalId,
    string TargetPlatform,
    string TargetQuery);

/// <summary>
///     Результат создания запроса на связывание аккаунтов.
/// </summary>
/// <param name="RequestId">Идентификатор созданного запроса или null, если запрос не создан.</param>
/// <param name="Status">Статус операции, одно из значений <see cref="LinkStatuses" />.</param>
/// <param name="Message">Текст для показа пользователю.</param>
/// <param name="Target">Найденный целевой аккаунт, если он определён.</param>
/// <param name="RetryAfter">Время, раньше которого повторная попытка не имеет смысла.</param>
public sealed record CreateLinkResult(
    Guid? RequestId,
    string Status,
    string Message,
    AccountDto? Target,
    DateTimeOffset? RetryAfter);

/// <summary>
///     Запрос на ответ по входящему предложению связать аккаунты.
/// </summary>
/// <param name="ResponderPlatform">Платформа отвечающего пользователя.</param>
/// <param name="ResponderExternalId">Идентификатор отвечающего пользователя на платформе.</param>
public sealed record RespondLinkRequest(
    string ResponderPlatform,
    string ResponderExternalId);

/// <summary>
///     Результат ответа на предложение связать аккаунты.
/// </summary>
/// <param name="Status">Статус операции, одно из значений <see cref="LinkStatuses" />.</param>
/// <param name="Message">Текст для показа пользователю.</param>
/// <param name="Source">Аккаунт инициатора связки, если он определён.</param>
/// <param name="Target">Целевой аккаунт связки, если он определён.</param>
public sealed record RespondLinkResult(
    string Status,
    string Message,
    AccountDto? Source,
    AccountDto? Target);

/// <summary>
///     Запрос администратора на принудительное связывание аккаунтов.
/// </summary>
/// <param name="SourcePlatform">Платформа аккаунта-инициатора.</param>
/// <param name="SourceExternalId">Идентификатор аккаунта-инициатора на платформе.</param>
/// <param name="TargetPlatform">Платформа целевого аккаунта.</param>
/// <param name="TargetQuery">Поисковый запрос для нахождения целевого аккаунта.</param>
public sealed record AdminLinkRequest(
    string SourcePlatform,
    string SourceExternalId,
    string TargetPlatform,
    string TargetQuery);

/// <summary>
///     Запрос на разрыв связки аккаунтов.
/// </summary>
/// <param name="Platform">Платформа аккаунта, для которого выполняется разрыв.</param>
/// <param name="ExternalId">Идентификатор аккаунта на платформе.</param>
public sealed record UnlinkRequest(
    string Platform,
    string ExternalId);

/// <summary>
///     Результат разрыва связки аккаунтов.
/// </summary>
/// <param name="Status">Статус операции, одно из значений <see cref="LinkStatuses" />.</param>
/// <param name="Message">Текст для показа пользователю.</param>
/// <param name="Account">Затронутый аккаунт, если он найден.</param>
public sealed record UnlinkResult(
    string Status,
    string Message,
    AccountDto? Account);

/// <summary>
///     Запрос с текстовой причиной, например для административных действий.
/// </summary>
/// <param name="Reason">Причина, указанная пользователем или администратором.</param>
public sealed record ReasonRequest(string Reason);

/// <summary>
///     Строковые статусы операций со связками аккаунтов, общие для всех сервисов.
/// </summary>
public static class LinkStatuses
{
    /// <summary>Предложение о связке отправлено и ожидает ответа.</summary>
    public const string Pending = "pending";

    /// <summary>Связка подтверждена.</summary>
    public const string Accepted = "accepted";

    /// <summary>Связка отклонена.</summary>
    public const string Declined = "declined";

    /// <summary>Аккаунт или запрос не найден.</summary>
    public const string NotFound = "not_found";

    /// <summary>Найдено несколько подходящих аккаунтов, нужен уточняющий запрос.</summary>
    public const string Ambiguous = "ambiguous";

    /// <summary>Пользователь запретил личные сообщения, поэтому связка невозможна.</summary>
    public const string Blocked = "blocked";

    /// <summary>Аккаунты уже связаны между собой.</summary>
    public const string AlreadyLinked = "already_linked";

    /// <summary>Повторная попытка возможна только после истечения паузы.</summary>
    public const string Cooldown = "cooldown";

    /// <summary>Попытка связать аккаунты одной и той же платформы.</summary>
    public const string SamePlatform = "same_platform";

    /// <summary>Срок действия предложения о связке истёк.</summary>
    public const string Expired = "expired";

    /// <summary>Операция завершилась ошибкой.</summary>
    public const string Failed = "failed";

    /// <summary>Операция успешно выполнена.</summary>
    public const string Ok = "ok";
}
