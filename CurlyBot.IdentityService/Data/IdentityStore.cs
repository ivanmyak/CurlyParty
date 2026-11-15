using System.Data;
using CurlyBot.Contracts.Identity;
using Npgsql;

namespace CurlyBot.IdentityService.Data;

/// <summary>
///     Хранилище учётных записей и связок между ними. Инкапсулирует работу с PostgreSQL:
///     создание схемы, чтение и обновление аккаунтов, а также обработку запросов на связывание.
/// </summary>
/// <param name="dataSource">Источник подключений к базе данных PostgreSQL.</param>
/// <param name="logger">Логгер для записи сведений о работе хранилища.</param>
public sealed class IdentityStore(NpgsqlDataSource dataSource, ILogger<IdentityStore> logger)
{
    private const string AccountColumns =
        "id, platform, external_id, username, display_name, link_group_id, dm_blocked, created_at, updated_at, last_contact_at, contact_count";

    private const string RequestColumns =
        "id, source_account_id, target_account_id, status, created_at, expires_at, responded_at, reason";

    /// <summary>Создаёт схему базы данных, если её ещё нет.</summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача инициализации схемы.</returns>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(DatabaseSchema.Sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
        logger.LogInformation("Схема базы IdentityService проверена/создана");
    }

    /// <summary>Создаёт или обновляет аккаунт и сбрасывает признак блокировки ЛС.</summary>
    /// <param name="platform">Имя платформы.</param>
    /// <param name="externalId">Идентификатор пользователя внутри платформы.</param>
    /// <param name="username">Имя пользователя (логин).</param>
    /// <param name="displayName">Отображаемое имя.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Актуальная запись аккаунта.</returns>
    public async Task<AccountRecord> UpsertAccountAsync(
        string platform,
        string externalId,
        string username,
        string displayName,
        CancellationToken cancellationToken)
    {
        platform = Contracts.Platforms.Normalize(platform);
        username = (username ?? string.Empty).Trim();
        displayName = (displayName ?? string.Empty).Trim();

        const string sql = $"""
            INSERT INTO platform_accounts
                (platform, external_id, username, username_normalized, display_name, display_name_normalized)
            VALUES
                (@platform, @external_id, @username, @username_norm, @display_name, @display_name_norm)
            ON CONFLICT (platform, external_id) DO UPDATE SET
                username = EXCLUDED.username,
                username_normalized = EXCLUDED.username_normalized,
                display_name = EXCLUDED.display_name,
                display_name_normalized = EXCLUDED.display_name_normalized,
                dm_blocked = false,
                updated_at = now()
            RETURNING {AccountColumns};
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("platform", platform);
        command.Parameters.AddWithValue("external_id", externalId);
        command.Parameters.AddWithValue("username", username);
        command.Parameters.AddWithValue("username_norm", AccountQuery.NormalizeUsername(username));
        command.Parameters.AddWithValue("display_name", displayName);
        command.Parameters.AddWithValue("display_name_norm", AccountQuery.NormalizeUsername(displayName));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return MapAccount(reader);
    }

    /// <summary>Обновляет время и счётчик последнего взаимодействия с ботом.</summary>
    /// <param name="platform">Имя платформы.</param>
    /// <param name="externalId">Идентификатор пользователя внутри платформы.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача обновления записи.</returns>
    public async Task TouchContactAsync(
        string platform,
        string externalId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE platform_accounts
            SET last_contact_at = now(),
                contact_count = contact_count + 1,
                dm_blocked = false,
                updated_at = now()
            WHERE platform = @platform AND external_id = @external_id;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("platform", Contracts.Platforms.Normalize(platform));
        command.Parameters.AddWithValue("external_id", externalId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Устанавливает признак блокировки личных сообщений.</summary>
    /// <param name="platform">Имя платформы.</param>
    /// <param name="externalId">Идентификатор пользователя внутри платформы.</param>
    /// <param name="blocked">Новое значение признака блокировки.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача обновления записи.</returns>
    public async Task SetDmBlockedAsync(
        string platform,
        string externalId,
        bool blocked,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE platform_accounts
            SET dm_blocked = @blocked,
                updated_at = now()
            WHERE platform = @platform AND external_id = @external_id;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("blocked", blocked);
        command.Parameters.AddWithValue("platform", Contracts.Platforms.Normalize(platform));
        command.Parameters.AddWithValue("external_id", externalId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Возвращает аккаунт по платформе и внешнему идентификатору.</summary>
    /// <param name="platform">Имя платформы.</param>
    /// <param name="externalId">Идентификатор пользователя внутри платформы.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Запись аккаунта или <see langword="null"/>, если аккаунт не найден.</returns>
    public async Task<AccountRecord?> GetAccountAsync(
        string platform,
        string externalId,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT {AccountColumns} FROM platform_accounts WHERE platform = @platform AND external_id = @external_id;";

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("platform", Contracts.Platforms.Normalize(platform));
        command.Parameters.AddWithValue("external_id", externalId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapAccount(reader) : null;
    }

    /// <summary>Ищет аккаунты платформы по разобранному запросу.</summary>
    /// <param name="platform">Имя платформы.</param>
    /// <param name="query">Разобранный поисковый запрос.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Список найденных аккаунтов, не более 25.</returns>
    public async Task<IReadOnlyList<AccountRecord>> FindAccountsAsync(
        string platform,
        ParsedAccountQuery query,
        CancellationToken cancellationToken)
    {
        var sql = query.Kind == AccountQueryKind.ExternalId
            ? $"SELECT {AccountColumns} FROM platform_accounts WHERE platform = @platform AND external_id = @value LIMIT 25;"
            : $"""
               SELECT {AccountColumns}
               FROM platform_accounts
               WHERE platform = @platform
                 AND (translate(username_normalized, 'ё', 'е') = @value
                      OR translate(display_name_normalized, 'ё', 'е') = @value)
               ORDER BY CASE WHEN translate(username_normalized, 'ё', 'е') = @value THEN 0 ELSE 1 END, display_name
               LIMIT 25;
               """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("platform", Contracts.Platforms.Normalize(platform));
        command.Parameters.AddWithValue("value", query.Value);

        var result = new List<AccountRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(MapAccount(reader));

        return result;
    }

    /// <summary>
    ///     Возвращает подсказки аккаунтов для автодополнения: при пустом запросе —
    ///     недавние контакты, иначе совпадения по нику и отображаемому имени.
    /// </summary>
    /// <param name="query">Введённый пользователем текст; пустая строка означает «недавние контакты».</param>
    /// <param name="limit">Максимальное количество подсказок (от 1 до 25).</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Аккаунты, упорядоченные от наиболее подходящих к менее подходящим.</returns>
    public async Task<IReadOnlyList<AccountRecord>> SuggestAccountsAsync(
        string? query,
        int limit,
        CancellationToken cancellationToken)
    {
        var normalized = AccountQuery.NormalizeUsername(query);
        limit = Math.Clamp(limit, 1, 25);

        var sql = $"""
            SELECT {AccountColumns}
            FROM platform_accounts
            WHERE @query = ''
               OR translate(username_normalized, 'ё', 'е') LIKE @query || '%'
               OR translate(display_name_normalized, 'ё', 'е') LIKE @query || '%'
               OR translate(display_name_normalized, 'ё', 'е') LIKE '%' || @query || '%'
            ORDER BY
                CASE
                    WHEN @query = '' THEN 0
                    WHEN translate(username_normalized, 'ё', 'е') = @query
                         OR translate(display_name_normalized, 'ё', 'е') = @query THEN 0
                    WHEN translate(username_normalized, 'ё', 'е') LIKE @query || '%'
                         OR translate(display_name_normalized, 'ё', 'е') LIKE @query || '%' THEN 1
                    ELSE 2
                END,
                last_contact_at DESC NULLS LAST,
                display_name
            LIMIT @limit;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("query", normalized);
        command.Parameters.AddWithValue("limit", limit);

        var result = new List<AccountRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(MapAccount(reader));

        return result;
    }

    /// <summary>Формирует профиль аккаунта вместе со связанными аккаунтами.</summary>
    /// <param name="platform">Имя платформы.</param>
    /// <param name="externalId">Идентификатор пользователя внутри платформы.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Профиль или <see langword="null"/>, если аккаунт не найден.</returns>
    public async Task<ProfileDto?> GetProfileAsync(
        string platform,
        string externalId,
        CancellationToken cancellationToken)
    {
        var account = await GetAccountAsync(platform, externalId, cancellationToken);
        if (account is null)
            return null;

        var linked = account.LinkGroupId is null
            ? Array.Empty<AccountRecord>()
            : await GetGroupMembersAsync(account.LinkGroupId.Value, cancellationToken);

        var linkedDtos = linked
            .Where(x => x.Id != account.Id)
            .Select(x => x.ToDto())
            .ToArray();

        return new ProfileDto(account.ToDto(), linkedDtos);
    }

    /// <summary>Возвращает все аккаунты, входящие в группу связки.</summary>
    /// <param name="groupId">Идентификатор группы связки.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Аккаунты группы, упорядоченные по платформе.</returns>
    public async Task<IReadOnlyList<AccountRecord>> GetGroupMembersAsync(
        Guid groupId,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT {AccountColumns} FROM platform_accounts WHERE link_group_id = @group_id ORDER BY platform;";

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("group_id", groupId);

        var result = new List<AccountRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(MapAccount(reader));

        return result;
    }

    /// <summary>
    ///     Пытается создать запрос на связку с учётом уже существующих связей,
    ///     активных запросов и периода ожидания после отказа.
    /// </summary>
    /// <param name="source">Аккаунт-инициатор.</param>
    /// <param name="target">Целевой аккаунт.</param>
    /// <param name="requestTtl">Срок жизни запроса.</param>
    /// <param name="declineCooldown">Период ожидания после отказа.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Результат попытки создания запроса.</returns>
    public async Task<LinkAttemptResult> TryCreateLinkRequestAsync(
        AccountRecord source,
        AccountRecord target,
        TimeSpan requestTtl,
        TimeSpan declineCooldown,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var locked = await GetAccountsForUpdateAsync(
            connection,
            transaction,
            new[] { source.Id, target.Id },
            cancellationToken);

        source = locked.First(x => x.Id == source.Id);
        target = locked.First(x => x.Id == target.Id);

        if (source.LinkGroupId is not null && source.LinkGroupId == target.LinkGroupId)
            return new LinkAttemptResult(null, LinkStatuses.AlreadyLinked);

        if (source.LinkGroupId is not null)
            return new LinkAttemptResult(null, LinkStatuses.AlreadyLinked, ExistingStatus: "source_linked");

        if (target.LinkGroupId is not null)
            return new LinkAttemptResult(null, LinkStatuses.AlreadyLinked, ExistingStatus: "target_linked");

        var cooldownUntil = await GetCooldownUntilAsync(connection, transaction, source.Id, target.Id, cancellationToken);
        if (cooldownUntil is not null && cooldownUntil > DateTimeOffset.UtcNow)
            return new LinkAttemptResult(null, LinkStatuses.Cooldown, cooldownUntil);

        var pending = await GetPendingRequestAsync(connection, transaction, source.Id, target.Id, cancellationToken);
        if (pending is not null)
            return new LinkAttemptResult(pending.Id, LinkStatuses.Pending, pending.ExpiresAt);

        var now = DateTimeOffset.UtcNow;
        var requestId = Guid.NewGuid();
        var expiresAt = now.Add(requestTtl);

        const string insertSql = """
            INSERT INTO link_requests (id, source_account_id, target_account_id, status, created_at, expires_at)
            VALUES (@id, @source_id, @target_id, 'pending', @created_at, @expires_at);
            """;

        await using (var insert = new NpgsqlCommand(insertSql, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", requestId);
            insert.Parameters.AddWithValue("source_id", source.Id);
            insert.Parameters.AddWithValue("target_id", target.Id);
            insert.Parameters.AddWithValue("created_at", now);
            insert.Parameters.AddWithValue("expires_at", expiresAt);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new LinkAttemptResult(requestId, LinkStatuses.Pending, expiresAt, Created: true);
    }

    /// <summary>Подтверждает запрос на связку и объединяет аккаунты в группу.</summary>
    /// <param name="requestId">Идентификатор запроса.</param>
    /// <param name="responderPlatform">Платформа отвечающего.</param>
    /// <param name="responderExternalId">Идентификатор отвечающего на его платформе.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Результат обработки запроса.</returns>
    public async Task<RespondOutcome> AcceptLinkRequestAsync(
        Guid requestId,
        string responderPlatform,
        string responderExternalId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var request = await GetRequestForUpdateAsync(connection, transaction, requestId, cancellationToken);
        if (request is null)
            return new RespondOutcome(LinkStatuses.NotFound, null, null, null);

        if (request.Status != LinkStatuses.Pending)
            return new RespondOutcome(request.Status, request, null, null);

        if (request.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await UpdateRequestStatusAsync(connection, transaction, requestId, LinkStatuses.Expired, "expired", cancellationToken);
            return new RespondOutcome(LinkStatuses.Expired, request, null, null);
        }

        var accounts = await GetAccountsForUpdateAsync(
            connection,
            transaction,
            new[] { request.SourceAccountId, request.TargetAccountId },
            cancellationToken);

        var source = accounts.First(x => x.Id == request.SourceAccountId);
        var target = accounts.First(x => x.Id == request.TargetAccountId);

        if (!string.Equals(target.Platform, Contracts.Platforms.Normalize(responderPlatform), StringComparison.Ordinal) ||
            !string.Equals(target.ExternalId, responderExternalId, StringComparison.Ordinal))
        {
            return new RespondOutcome("forbidden", request, source, target);
        }

        if (source.LinkGroupId is not null && target.LinkGroupId is not null && source.LinkGroupId != target.LinkGroupId)
            return new RespondOutcome(LinkStatuses.AlreadyLinked, request, source, target);

        var groupId = source.LinkGroupId ?? target.LinkGroupId ?? Guid.NewGuid();

        const string linkSql = """
            UPDATE platform_accounts
            SET link_group_id = @group_id, updated_at = now()
            WHERE id = ANY(@ids);
            """;

        await using (var link = new NpgsqlCommand(linkSql, connection, transaction))
        {
            link.Parameters.AddWithValue("group_id", groupId);
            link.Parameters.AddWithValue("ids", new[] { source.Id, target.Id });
            await link.ExecuteNonQueryAsync(cancellationToken);
        }

        await UpdateRequestStatusAsync(connection, transaction, requestId, LinkStatuses.Accepted, "accepted", cancellationToken);
        await DeleteCooldownAsync(connection, transaction, source.Id, target.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var updatedSource = source with { LinkGroupId = groupId };
        var updatedTarget = target with { LinkGroupId = groupId };
        return new RespondOutcome(LinkStatuses.Accepted, request with { Status = LinkStatuses.Accepted }, updatedSource, updatedTarget);
    }

    /// <summary>Отклоняет запрос на связку и включает период ожидания для повторной попытки.</summary>
    /// <param name="requestId">Идентификатор запроса.</param>
    /// <param name="responderPlatform">Платформа отвечающего.</param>
    /// <param name="responderExternalId">Идентификатор отвечающего на его платформе.</param>
    /// <param name="declineCooldown">Длительность периода ожидания.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Результат обработки запроса.</returns>
    public async Task<RespondOutcome> DeclineLinkRequestAsync(
        Guid requestId,
        string responderPlatform,
        string responderExternalId,
        TimeSpan declineCooldown,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var request = await GetRequestForUpdateAsync(connection, transaction, requestId, cancellationToken);
        if (request is null)
            return new RespondOutcome(LinkStatuses.NotFound, null, null, null);

        if (request.Status != LinkStatuses.Pending)
            return new RespondOutcome(request.Status, request, null, null);

        var accounts = await GetAccountsForUpdateAsync(
            connection,
            transaction,
            new[] { request.SourceAccountId, request.TargetAccountId },
            cancellationToken);

        var source = accounts.First(x => x.Id == request.SourceAccountId);
        var target = accounts.First(x => x.Id == request.TargetAccountId);

        if (!string.Equals(target.Platform, Contracts.Platforms.Normalize(responderPlatform), StringComparison.Ordinal) ||
            !string.Equals(target.ExternalId, responderExternalId, StringComparison.Ordinal))
        {
            return new RespondOutcome("forbidden", request, source, target);
        }

        await UpdateRequestStatusAsync(connection, transaction, requestId, LinkStatuses.Declined, "declined", cancellationToken);

        const string cooldownSql = """
            INSERT INTO link_cooldowns (source_account_id, target_account_id, until)
            VALUES (@source_id, @target_id, @until)
            ON CONFLICT (source_account_id, target_account_id)
            DO UPDATE SET until = EXCLUDED.until;
            """;

        await using (var cooldown = new NpgsqlCommand(cooldownSql, connection, transaction))
        {
            cooldown.Parameters.AddWithValue("source_id", source.Id);
            cooldown.Parameters.AddWithValue("target_id", target.Id);
            cooldown.Parameters.AddWithValue("until", DateTimeOffset.UtcNow.Add(declineCooldown));
            await cooldown.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new RespondOutcome(LinkStatuses.Declined, request with { Status = LinkStatuses.Declined }, source, target);
    }

    /// <summary>Помечает запрос как не доставленный получателю.</summary>
    /// <param name="requestId">Идентификатор запроса.</param>
    /// <param name="reason">Причина неудачной доставки.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Результат обновления запроса.</returns>
    public async Task<NotificationFailedOutcome> MarkNotificationFailedAsync(
        Guid requestId,
        string reason,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var request = await GetRequestForUpdateAsync(connection, transaction, requestId, cancellationToken);
        if (request is null)
            return new NotificationFailedOutcome(false, null, null, null);

        var accounts = await GetAccountsForUpdateAsync(
            connection,
            transaction,
            new[] { request.SourceAccountId, request.TargetAccountId },
            cancellationToken);

        var source = accounts.First(x => x.Id == request.SourceAccountId);
        var target = accounts.First(x => x.Id == request.TargetAccountId);

        var updatedStatus = request.Status;
        if (request.Status == LinkStatuses.Pending)
        {
            await UpdateRequestStatusAsync(connection, transaction, requestId, LinkStatuses.Failed, reason, cancellationToken);
            updatedStatus = LinkStatuses.Failed;
        }

        await transaction.CommitAsync(cancellationToken);
        return new NotificationFailedOutcome(true, request with { Status = updatedStatus, Reason = reason }, source, target);
    }

    /// <summary>Принудительно связывает аккаунты без подтверждения получателя.</summary>
    /// <param name="source">Аккаунт-инициатор.</param>
    /// <param name="target">Целевой аккаунт.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Результат связывания.</returns>
    public async Task<RespondOutcome> ForceLinkAsync(
        AccountRecord source,
        AccountRecord target,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var accounts = await GetAccountsForUpdateAsync(
            connection,
            transaction,
            new[] { source.Id, target.Id },
            cancellationToken);

        source = accounts.First(x => x.Id == source.Id);
        target = accounts.First(x => x.Id == target.Id);

        if (source.LinkGroupId is not null && source.LinkGroupId == target.LinkGroupId)
            return new RespondOutcome(LinkStatuses.AlreadyLinked, null, source, target);

        var groupId = source.LinkGroupId ?? Guid.NewGuid();

        const string updateSql = """
            UPDATE platform_accounts
            SET link_group_id = @group_id, updated_at = now()
            WHERE id = ANY(@ids);
            """;

        await using (var update = new NpgsqlCommand(updateSql, connection, transaction))
        {
            update.Parameters.AddWithValue("group_id", groupId);
            update.Parameters.AddWithValue("ids", new[] { source.Id, target.Id });
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        const string approveSql = """
            UPDATE link_requests
            SET status = 'accepted', responded_at = now(), reason = 'admin'
            WHERE ((source_account_id = @source_id AND target_account_id = @target_id)
                OR (source_account_id = @target_id AND target_account_id = @source_id))
              AND status = 'pending';
            """;

        await using (var approve = new NpgsqlCommand(approveSql, connection, transaction))
        {
            approve.Parameters.AddWithValue("source_id", source.Id);
            approve.Parameters.AddWithValue("target_id", target.Id);
            await approve.ExecuteNonQueryAsync(cancellationToken);
        }

        await DeleteCooldownAsync(connection, transaction, source.Id, target.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new RespondOutcome(
            LinkStatuses.Accepted,
            null,
            source with { LinkGroupId = groupId },
            target with { LinkGroupId = groupId });
    }

    /// <summary>Разрывает связку аккаунта и очищает опустевшую группу.</summary>
    /// <param name="platform">Имя платформы.</param>
    /// <param name="externalId">Идентификатор пользователя внутри платформы.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Результат разрыва связки.</returns>
    public async Task<UnlinkOutcome> UnlinkAsync(
        string platform,
        string externalId,
        CancellationToken cancellationToken)
    {
        platform = Contracts.Platforms.Normalize(platform);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var account = await GetAccountForUpdateAsync(connection, transaction, platform, externalId, cancellationToken);
        if (account is null)
            return new UnlinkOutcome(LinkStatuses.NotFound, null);

        if (account.LinkGroupId is null)
            return new UnlinkOutcome(LinkStatuses.Ok, account);

        var groupId = account.LinkGroupId.Value;

        const string unlinkSql = """
            UPDATE platform_accounts
            SET link_group_id = NULL, updated_at = now()
            WHERE id = @id;
            """;

        await using (var unlink = new NpgsqlCommand(unlinkSql, connection, transaction))
        {
            unlink.Parameters.AddWithValue("id", account.Id);
            await unlink.ExecuteNonQueryAsync(cancellationToken);
        }

        const string cleanupSql = """
            UPDATE platform_accounts
            SET link_group_id = NULL, updated_at = now()
            WHERE link_group_id = @group_id
              AND (SELECT count(*) FROM platform_accounts WHERE link_group_id = @group_id) <= 1;
            """;

        await using (var cleanup = new NpgsqlCommand(cleanupSql, connection, transaction))
        {
            cleanup.Parameters.AddWithValue("group_id", groupId);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new UnlinkOutcome(LinkStatuses.Ok, account with { LinkGroupId = null });
    }

    private static async Task<AccountRecord[]> GetAccountsForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid[] ids,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT {AccountColumns} FROM platform_accounts WHERE id = ANY(@ids) FOR UPDATE;";

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("ids", ids);

        var result = new List<AccountRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(MapAccount(reader));

        return result.ToArray();
    }

    private static async Task<AccountRecord?> GetAccountForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string platform,
        string externalId,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT {AccountColumns} FROM platform_accounts WHERE platform = @platform AND external_id = @external_id FOR UPDATE;";

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("platform", platform);
        command.Parameters.AddWithValue("external_id", externalId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapAccount(reader) : null;
    }

    private static async Task<LinkRequestRecord?> GetRequestForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT {RequestColumns} FROM link_requests WHERE id = @id FOR UPDATE;";

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", requestId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapRequest(reader) : null;
    }

    private static async Task<LinkRequestRecord?> GetPendingRequestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sourceId,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT {RequestColumns}
            FROM link_requests
            WHERE source_account_id = @source_id
              AND target_account_id = @target_id
              AND status = 'pending'
              AND expires_at > now()
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("source_id", sourceId);
        command.Parameters.AddWithValue("target_id", targetId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapRequest(reader) : null;
    }

    private static async Task<DateTimeOffset?> GetCooldownUntilAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sourceId,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT until FROM link_cooldowns
            WHERE source_account_id = @source_id AND target_account_id = @target_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("source_id", sourceId);
        command.Parameters.AddWithValue("target_id", targetId);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DateTime dateTime
            ? new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc))
            : null;
    }

    private static async Task UpdateRequestStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid requestId,
        string status,
        string reason,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE link_requests
            SET status = @status, responded_at = now(), reason = @reason
            WHERE id = @id;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("id", requestId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteCooldownAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sourceId,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM link_cooldowns
            WHERE source_account_id = @source_id AND target_account_id = @target_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("source_id", sourceId);
        command.Parameters.AddWithValue("target_id", targetId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static AccountRecord MapAccount(NpgsqlDataReader reader)
        => new(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetGuid(5),
            reader.GetBoolean(6),
            ReadTimestamp(reader, 7),
            ReadTimestamp(reader, 8),
            reader.IsDBNull(9) ? null : ReadTimestamp(reader, 9),
            reader.GetInt64(10));

    private static LinkRequestRecord MapRequest(NpgsqlDataReader reader)
        => new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetString(3),
            ReadTimestamp(reader, 4),
            ReadTimestamp(reader, 5),
            reader.IsDBNull(6) ? null : ReadTimestamp(reader, 6),
            reader.IsDBNull(7) ? null : reader.GetString(7));

    private static DateTimeOffset ReadTimestamp(NpgsqlDataReader reader, int ordinal)
        => new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
}

/// <summary>Результат разрыва связки аккаунта.</summary>
/// <param name="Status">Статус операции.</param>
/// <param name="Account">Аккаунт после изменения; <see langword="null"/>, если аккаунт не найден.</param>
public sealed record UnlinkOutcome(string Status, AccountRecord? Account);
