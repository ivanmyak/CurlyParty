namespace CurlyBot.IdentityService.Data;

/// <summary>
///     Запись учётной записи на внешней платформе, соответствующая строке таблицы
///     <c>platform_accounts</c>.
/// </summary>
/// <param name="Id">Уникальный идентификатор аккаунта в базе данных.</param>
/// <param name="Platform">Нормализованное имя платформы, например <c>telegram</c> или <c>discord</c>.</param>
/// <param name="ExternalId">Идентификатор пользователя в рамках его платформы.</param>
/// <param name="Username">Имя пользователя (логин) на платформе.</param>
/// <param name="DisplayName">Отображаемое имя пользователя на платформе.</param>
/// <param name="LinkGroupId">Идентификатор группы связанных аккаунтов; <see langword="null"/>, если аккаунт не связан.</param>
/// <param name="DmBlocked">Признак того, что пользователь заблокировал бота или личные сообщения.</param>
/// <param name="CreatedAt">Момент создания записи об аккаунте.</param>
/// <param name="UpdatedAt">Момент последнего обновления записи.</param>
/// <param name="LastContactAt">Момент последнего взаимодействия с ботом; <see langword="null"/>, если взаимодействий ещё не было.</param>
/// <param name="ContactCount">Общее количество зафиксированных взаимодействий с ботом.</param>
public sealed record AccountRecord(
    Guid Id,
    string Platform,
    string ExternalId,
    string Username,
    string DisplayName,
    Guid? LinkGroupId,
    bool DmBlocked,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastContactAt,
    long ContactCount)
{
    /// <summary>
    ///     Преобразует запись аккаунта в транспортный объект <c>AccountDto</c>.
    /// </summary>
    /// <returns>DTO аккаунта для передачи между сервисами.</returns>
    public global::CurlyBot.Contracts.Identity.AccountDto ToDto()
        => new(Platform, ExternalId, Username, DisplayName, LinkGroupId?.ToString(), DmBlocked);
}

/// <summary>
///     Запись запроса на связывание двух аккаунтов, соответствующая строке таблицы
///     <c>link_requests</c>.
/// </summary>
/// <param name="Id">Уникальный идентификатор запроса на связывание.</param>
/// <param name="SourceAccountId">Идентификатор аккаунта, инициировавшего запрос.</param>
/// <param name="TargetAccountId">Идентификатор аккаунта, которому адресован запрос.</param>
/// <param name="Status">Текущий статус запроса, например <c>pending</c> или <c>accepted</c>.</param>
/// <param name="CreatedAt">Момент создания запроса.</param>
/// <param name="ExpiresAt">Момент, после которого запрос считается просроченным.</param>
/// <param name="RespondedAt">Момент ответа на запрос; <see langword="null"/>, если ответа ещё не было.</param>
/// <param name="Reason">Причина отклонения или ошибки обработки; <see langword="null"/>, если причина отсутствует.</param>
public sealed record LinkRequestRecord(
    Guid Id,
    Guid SourceAccountId,
    Guid TargetAccountId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RespondedAt,
    string? Reason);

/// <summary>
///     Результат попытки создать запрос на связывание аккаунтов.
/// </summary>
/// <param name="RequestId">Идентификатор созданного или уже существующего запроса; <see langword="null"/>, если запрос не создан.</param>
/// <param name="Status">Итоговый статус операции, например <c>pending</c>, <c>cooldown</c> или <c>already_linked</c>.</param>
/// <param name="RetryAfter">Момент, раньше которого повторная попытка недоступна; <see langword="null"/>, если ограничения нет.</param>
/// <param name="ExistingStatus">Уточнение причины отказа при уже существующей связке; <see langword="null"/>, если уточнение не требуется.</param>
/// <param name="Created">Признак того, что запрос был создан впервые, а не найден среди существующих.</param>
public sealed record LinkAttemptResult(
    Guid? RequestId,
    string Status,
    DateTimeOffset? RetryAfter = null,
    string? ExistingStatus = null,
    bool Created = false);

/// <summary>
///     Результат обработки ответа на запрос связывания аккаунтов.
/// </summary>
/// <param name="Status">Статус обработки запроса, например <c>accepted</c>, <c>declined</c> или <c>not_found</c>.</param>
/// <param name="Request">Актуальная запись запроса; <see langword="null"/>, если запрос не найден.</param>
/// <param name="Source">Аккаунт, инициировавший запрос; <see langword="null"/>, если он не был загружен.</param>
/// <param name="Target">Аккаунт, которому адресован запрос; <see langword="null"/>, если он не был загружен.</param>
public sealed record RespondOutcome(
    string Status,
    LinkRequestRecord? Request,
    AccountRecord? Source,
    AccountRecord? Target);

/// <summary>
///     Результат обработки события о неудачной доставке уведомления по запросу связывания.
/// </summary>
/// <param name="Found">Признак того, что запрос найден и обработан.</param>
/// <param name="Request">Запись запроса с обновлённым статусом; <see langword="null"/>, если запрос не найден.</param>
/// <param name="Source">Аккаунт, инициировавший запрос; <see langword="null"/>, если он не был загружен.</param>
/// <param name="Target">Аккаунт, которому адресован запрос; <see langword="null"/>, если он не был загружен.</param>
public sealed record NotificationFailedOutcome(
    bool Found,
    LinkRequestRecord? Request,
    AccountRecord? Source,
    AccountRecord? Target);
