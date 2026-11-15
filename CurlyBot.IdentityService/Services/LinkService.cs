using CurlyBot.Contracts;
using CurlyBot.Contracts.Identity;
using CurlyBot.Contracts.Messaging;
using CurlyBot.IdentityService.Data;
using CurlyBot.IdentityService.Options;
using CurlyBot.Messaging;
using Microsoft.Extensions.Options;

namespace CurlyBot.IdentityService.Services;

/// <summary>
///     Логика связывания аккаунтов: поиск пользователей, создание запросов,
///     подтверждение и отклонение, административное связывание и разрыв связок.
/// </summary>
/// <param name="store">Хранилище аккаунтов и запросов.</param>
/// <param name="publisher">Публикатор событий связки.</param>
/// <param name="options">Настройки сроков жизни запросов и периода ожидания.</param>
/// <param name="logger">Журнал сервиса.</param>
public sealed class LinkService(
    IdentityStore store,
    IIntegrationEventPublisher publisher,
    IOptions<IdentityLinkOptions> options,
    ILogger<LinkService> logger)
{
    private readonly IdentityLinkOptions _options = options.Value;

    /// <summary>Ищет аккаунты по всем платформам.</summary>
    /// <param name="query">Поисковый запрос.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Найденные аккаунты и признак неоднозначности.</returns>
    public async Task<FindAccountsResponse> FindAsync(string query, CancellationToken cancellationToken)
    {
        var accounts = await FindAcrossPlatformsAsync(query, cancellationToken);
        return new FindAccountsResponse(accounts.Select(x => x.ToDto()).ToArray(), accounts.Count > 1);
    }

    /// <summary>Создаёт запрос на связку и уведомляет целевого пользователя.</summary>
    /// <param name="request">Параметры запроса.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Результат создания запроса.</returns>
    public async Task<CreateLinkResult> CreateRequestAsync(
        CreateLinkRequest request,
        CancellationToken cancellationToken)
    {
        var sourcePlatform = Platforms.Normalize(request.SourcePlatform);
        var targetPlatform = Platforms.Normalize(request.TargetPlatform);

        var source = await store.GetAccountAsync(sourcePlatform, request.SourceExternalId, cancellationToken);
        if (source is null)
        {
            return new CreateLinkResult(
                null,
                LinkStatuses.NotFound,
                "Сначала напишите боту или вызовите /start, чтобы он знал ваш аккаунт.",
                null,
                null);
        }

        if (sourcePlatform == targetPlatform)
        {
            return new CreateLinkResult(
                null,
                LinkStatuses.SamePlatform,
                "Нельзя связать два аккаунта одной платформы. Укажите аккаунт другой платформы.",
                null,
                null);
        }

        var matches = await FindForPlatformAsync(targetPlatform, request.TargetQuery, cancellationToken);
        if (matches.Count == 0)
        {
            return new CreateLinkResult(
                null,
                LinkStatuses.NotFound,
                $"Аккаунт в {Platforms.DisplayName(targetPlatform)} не найден. " +
                "Пользователь должен сначала запустить бота (/start в Telegram) и проверить @username или ссылку на профиль.",
                null,
                null);
        }

        if (matches.Count > 1)
        {
            var variants = string.Join(", ", matches.Select(x => $"@{x.Username} ({x.DisplayName})"));
            return new CreateLinkResult(
                null,
                LinkStatuses.Ambiguous,
                $"Найдено несколько аккаунтов: {variants}. Укажите прямую ссылку на профиль или числовой ID.",
                null,
                null);
        }

        var target = matches[0];

        if (target.DmBlocked)
        {
            return new CreateLinkResult(
                null,
                LinkStatuses.Blocked,
                $"Пользователь {target.DisplayName} заблокировал бота или закрыл личные сообщения.",
                target.ToDto(),
                null);
        }

        if (source.LinkGroupId is not null && source.LinkGroupId == target.LinkGroupId)
        {
            return new CreateLinkResult(
                null,
                LinkStatuses.AlreadyLinked,
                "Эти аккаунты уже связаны.",
                target.ToDto(),
                null);
        }

        if (source.LinkGroupId is not null)
        {
            return new CreateLinkResult(
                null,
                LinkStatuses.AlreadyLinked,
                "Ваш аккаунт уже связан с другим пользователем. Сначала выполните /unlinkme.",
                target.ToDto(),
                null);
        }

        if (target.LinkGroupId is not null)
        {
            return new CreateLinkResult(
                null,
                LinkStatuses.AlreadyLinked,
                $"Аккаунт {target.DisplayName} уже связан с другим пользователем.",
                target.ToDto(),
                null);
        }

        var attempt = await store.TryCreateLinkRequestAsync(
            source,
            target,
            TimeSpan.FromMinutes(_options.RequestTtlMinutes),
            TimeSpan.FromMinutes(_options.DeclineCooldownMinutes),
            cancellationToken);

        if (attempt.Status == LinkStatuses.Cooldown)
        {
            var until = attempt.RetryAfter?.ToString("dd.MM.yyyy HH:mm 'UTC'") ?? "позже";
            return new CreateLinkResult(
                null,
                LinkStatuses.Cooldown,
                $"Пользователь отклонил предыдущий запрос. Повторить можно после {until}.",
                target.ToDto(),
                attempt.RetryAfter);
        }

        if (attempt.Status == LinkStatuses.AlreadyLinked)
        {
            return new CreateLinkResult(
                null,
                LinkStatuses.AlreadyLinked,
                "Один из аккаунтов уже связан. Сначала разорвите старую связку.",
                target.ToDto(),
                null);
        }

        if (attempt.RequestId is null)
        {
            return new CreateLinkResult(
                null,
                LinkStatuses.Failed,
                "Не удалось создать запрос на связку.",
                target.ToDto(),
                null);
        }

        if (attempt.Created)
        {
            await publisher.PublishAsync(
                RabbitTopology.LinkConfirmationRoutingKey(target.Platform),
                new LinkConfirmationRequested(
                    attempt.RequestId.Value,
                    target.Platform,
                    target.ExternalId,
                    target.DisplayName,
                    source.Platform,
                    source.ExternalId,
                    source.DisplayName,
                    source.Username,
                    attempt.RetryAfter ?? DateTimeOffset.UtcNow.AddMinutes(_options.RequestTtlMinutes)),
                cancellationToken);

            logger.LogInformation(
                "Создан запрос связки {RequestId}: {SourcePlatform}/{SourceId} -> {TargetPlatform}/{TargetId}",
                attempt.RequestId,
                source.Platform,
                source.ExternalId,
                target.Platform,
                target.ExternalId);

            return new CreateLinkResult(
                attempt.RequestId,
                LinkStatuses.Pending,
                $"Запрос на связку отправлен пользователю {target.DisplayName} в {Platforms.DisplayName(target.Platform)}. " +
                "Подтвердите его в личных сообщениях.",
                target.ToDto(),
                attempt.RetryAfter);
        }

        return new CreateLinkResult(
            attempt.RequestId,
            LinkStatuses.Pending,
            $"Запрос уже отправлен пользователю {target.DisplayName}. Ожидайте подтверждения.",
            target.ToDto(),
            attempt.RetryAfter);
    }

    /// <summary>Подтверждает запрос на связку и уведомляет инициатора.</summary>
    /// <param name="requestId">Идентификатор запроса.</param>
    /// <param name="request">Данные отвечающего пользователя.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Результат подтверждения.</returns>
    public async Task<RespondLinkResult> AcceptAsync(
        Guid requestId,
        RespondLinkRequest request,
        CancellationToken cancellationToken)
    {
        var outcome = await store.AcceptLinkRequestAsync(
            requestId,
            request.ResponderPlatform,
            request.ResponderExternalId,
            cancellationToken);

        if (outcome.Status == LinkStatuses.Accepted && outcome.Source is not null)
        {
            await PublishResultAsync(outcome.Source, requestId, true, null, cancellationToken);
            return new RespondLinkResult(
                LinkStatuses.Accepted,
                "Аккаунты успешно связаны.",
                outcome.Source.ToDto(),
                outcome.Target?.ToDto());
        }

        return new RespondLinkResult(
            outcome.Status,
            StatusMessage(outcome.Status),
            outcome.Source?.ToDto(),
            outcome.Target?.ToDto());
    }

    /// <summary>Отклоняет запрос на связку и уведомляет инициатора.</summary>
    /// <param name="requestId">Идентификатор запроса.</param>
    /// <param name="request">Данные отвечающего пользователя.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Результат отклонения.</returns>
    public async Task<RespondLinkResult> DeclineAsync(
        Guid requestId,
        RespondLinkRequest request,
        CancellationToken cancellationToken)
    {
        var outcome = await store.DeclineLinkRequestAsync(
            requestId,
            request.ResponderPlatform,
            request.ResponderExternalId,
            TimeSpan.FromMinutes(_options.DeclineCooldownMinutes),
            cancellationToken);

        if (outcome.Status == LinkStatuses.Declined && outcome.Source is not null)
        {
            await PublishResultAsync(
                outcome.Source,
                requestId,
                false,
                "Пользователь отклонил запрос на связку. Повторить можно через " +
                $"{_options.DeclineCooldownMinutes} мин. или через администратора.",
                cancellationToken);

            return new RespondLinkResult(
                LinkStatuses.Declined,
                "Запрос отклонён. Повторная попытка будет доступна позже.",
                outcome.Source.ToDto(),
                outcome.Target?.ToDto());
        }

        return new RespondLinkResult(
            outcome.Status,
            StatusMessage(outcome.Status),
            outcome.Source?.ToDto(),
            outcome.Target?.ToDto());
    }

    /// <summary>Отмечает, что уведомление о запросе не удалось доставить, и уведомляет инициатора.</summary>
    /// <param name="requestId">Идентификатор запроса.</param>
    /// <param name="reason">Причина неудачи.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача обработки.</returns>
    public async Task NotificationFailedAsync(
        Guid requestId,
        string reason,
        CancellationToken cancellationToken)
    {
        var outcome = await store.MarkNotificationFailedAsync(requestId, reason, cancellationToken);
        if (!outcome.Found || outcome.Source is null)
            return;

        await PublishResultAsync(
            outcome.Source,
            requestId,
            false,
            $"Не удалось доставить запрос на связку: {reason}",
            cancellationToken);
    }

    /// <summary>Связывает аккаунты по команде администратора, без подтверждения.</summary>
    /// <param name="request">Параметры административного связывания.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Результат связывания.</returns>
    public async Task<RespondLinkResult> AdminLinkAsync(
        AdminLinkRequest request,
        CancellationToken cancellationToken)
    {
        var sourcePlatform = Platforms.Normalize(request.SourcePlatform);
        var targetPlatform = Platforms.Normalize(request.TargetPlatform);

        var source = await store.GetAccountAsync(sourcePlatform, request.SourceExternalId, cancellationToken);
        if (source is null)
        {
            return new RespondLinkResult(
                LinkStatuses.NotFound,
                "Исходный аккаунт не найден в базе. Пользователь должен сначала взаимодействовать с ботом.",
                null,
                null);
        }

        if (sourcePlatform == targetPlatform)
        {
            return new RespondLinkResult(
                LinkStatuses.SamePlatform,
                "Нельзя связать два аккаунта одной платформы.",
                source.ToDto(),
                null);
        }

        var matches = await FindForPlatformAsync(targetPlatform, request.TargetQuery, cancellationToken);
        if (matches.Count == 0)
        {
            return new RespondLinkResult(
                LinkStatuses.NotFound,
                $"Аккаунт в {Platforms.DisplayName(targetPlatform)} не найден.",
                source.ToDto(),
                null);
        }

        if (matches.Count > 1)
        {
            var variants = string.Join(", ", matches.Select(x => $"@{x.Username} ({x.DisplayName})"));
            return new RespondLinkResult(
                LinkStatuses.Ambiguous,
                $"Найдено несколько аккаунтов: {variants}. Укажите прямую ссылку или ID.",
                source.ToDto(),
                null);
        }

        var target = matches[0];
        var outcome = await store.ForceLinkAsync(source, target, cancellationToken);

        if (outcome.Status == LinkStatuses.Accepted || outcome.Status == LinkStatuses.AlreadyLinked)
        {
            return new RespondLinkResult(
                LinkStatuses.Accepted,
                outcome.Status == LinkStatuses.AlreadyLinked
                    ? "Эти аккаунты уже связаны."
                    : $"Аккаунты {source.DisplayName} и {target.DisplayName} связаны администратором.",
                outcome.Source?.ToDto(),
                outcome.Target?.ToDto());
        }

        return new RespondLinkResult(
            outcome.Status,
            StatusMessage(outcome.Status),
            outcome.Source?.ToDto(),
            outcome.Target?.ToDto());
    }

    /// <summary>Разрывает связку указанного аккаунта.</summary>
    /// <param name="request">Данные аккаунта для разрыва связки.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Результат разрыва связки.</returns>
    public async Task<UnlinkResult> UnlinkAsync(
        UnlinkRequest request,
        CancellationToken cancellationToken)
    {
        var outcome = await store.UnlinkAsync(request.Platform, request.ExternalId, cancellationToken);
        return outcome.Status == LinkStatuses.Ok
            ? new UnlinkResult(LinkStatuses.Ok, "Связка аккаунтов разорвана.", outcome.Account?.ToDto())
            : new UnlinkResult(outcome.Status, StatusMessage(outcome.Status), outcome.Account?.ToDto());
    }

    private async Task<List<AccountRecord>> FindAcrossPlatformsAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<(string Platform, string ExternalId), AccountRecord>();

        foreach (var platform in AccountQuery.ResolveSearchPlatforms(query))
        {
            var parsed = AccountQuery.Parse(platform, query);
            var matches = await store.FindAccountsAsync(platform, parsed, cancellationToken);
            foreach (var match in matches)
                results[(match.Platform, match.ExternalId)] = match;
        }

        return results.Values
            .OrderBy(x => x.Platform)
            .ThenBy(x => x.DisplayName)
            .ToList();
    }

    private async Task<List<AccountRecord>> FindForPlatformAsync(
        string platform,
        string query,
        CancellationToken cancellationToken)
    {
        var parsed = AccountQuery.Parse(platform, query);
        var matches = await store.FindAccountsAsync(platform, parsed, cancellationToken);
        return matches.ToList();
    }

    private Task PublishResultAsync(
        AccountRecord source,
        Guid requestId,
        bool accepted,
        string? reason,
        CancellationToken cancellationToken)
        => publisher.PublishAsync(
            RabbitTopology.LinkResultRoutingKey(source.Platform),
            new LinkResultNotification(
                requestId,
                source.Platform,
                source.ExternalId,
                source.DisplayName,
                accepted,
                reason),
            cancellationToken);

    private static string StatusMessage(string status)
        => status switch
        {
            LinkStatuses.NotFound => "Запрос на связку не найден.",
            LinkStatuses.Expired => "Срок действия запроса истёк. Создайте новый запрос.",
            LinkStatuses.AlreadyLinked => "Один из аккаунтов уже связан.",
            LinkStatuses.Cooldown => "Повторная попытка пока заблокирована.",
            LinkStatuses.Declined => "Запрос уже был отклонён.",
            LinkStatuses.Accepted => "Запрос уже был подтверждён.",
            LinkStatuses.Failed => "Запрос завершился ошибкой.",
            "forbidden" => "Этот запрос адресован другому пользователю.",
            _ => $"Статус запроса: {status}"
        };
}
