using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CurlyBot.Contracts.Identity;
using CurlyBot.Contracts.Models;

namespace CurlyBot.Clients;

/// <summary>
///     HTTP-клиент единого сервиса идентификации.
///     Боты используют его для регистрации аккаунтов, поиска пользователей,
///     создания/подтверждения связок и отметки заблокированных ЛС.
/// </summary>
public sealed class IdentityServiceClient
{
    private readonly HttpClient _http;
    private readonly ILogger<IdentityServiceClient> _logger;

    /// <summary>Создаёт клиент поверх подготовленного <see cref="HttpClient"/>.</summary>
    /// <param name="http">HTTP-клиент с базовым адресом IdentityService.</param>
    /// <param name="logger">Журнал клиента.</param>
    public IdentityServiceClient(HttpClient http, ILogger<IdentityServiceClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>Регистрирует или обновляет аккаунт по платформо-независимому описанию пользователя.</summary>
    /// <param name="user">Пользователь, которого нужно сохранить в IdentityService.</param>
    /// <param name="cancellationToken">Токен отмены запроса.</param>
    /// <returns>Сохранённый аккаунт.</returns>
    public Task<AccountDto> UpsertAccountAsync(PlatformUser user, CancellationToken cancellationToken = default)
        => UpsertAccountAsync(
            user.Platform,
            user.UserId,
            user.Username ?? user.DisplayName,
            user.DisplayName,
            cancellationToken);

    /// <summary>Регистрирует или обновляет аккаунт по явно заданным полям.</summary>
    /// <param name="platform">Имя платформы, например <c>discord</c> или <c>telegram</c>.</param>
    /// <param name="externalId">Идентификатор пользователя внутри платформы.</param>
    /// <param name="username">Имя пользователя (логин) на платформе.</param>
    /// <param name="displayName">Отображаемое имя пользователя.</param>
    /// <param name="cancellationToken">Токен отмены запроса.</param>
    /// <returns>Сохранённый аккаунт.</returns>
    public async Task<AccountDto> UpsertAccountAsync(
        string platform,
        string externalId,
        string username,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var request = new UpsertAccountRequest(platform, externalId, username, displayName);
        using var response = await _http.PostAsJsonAsync("api/accounts/upsert", request, cancellationToken);
        return await ReadAsync<AccountDto>(response, cancellationToken);
    }

    /// <summary>Отмечает факт последнего взаимодействия пользователя с ботом.</summary>
    /// <param name="platform">Имя платформы.</param>
    /// <param name="externalId">Идентификатор пользователя внутри платформы.</param>
    /// <param name="cancellationToken">Токен отмены запроса.</param>
    /// <returns>Задача выполнения запроса.</returns>
    public async Task TouchContactAsync(
        string platform,
        string externalId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(
            "api/accounts/touch",
            new TouchContactRequest(platform, externalId),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>Сохраняет признак того, что пользователь заблокировал личные сообщения бота.</summary>
    /// <param name="platform">Имя платформы.</param>
    /// <param name="externalId">Идентификатор пользователя внутри платформы.</param>
    /// <param name="blocked">Признак блокировки ЛС.</param>
    /// <param name="cancellationToken">Токен отмены запроса.</param>
    /// <returns>Задача выполнения запроса.</returns>
    public async Task SetDmBlockedAsync(
        string platform,
        string externalId,
        bool blocked,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(
            "api/accounts/blocked",
            new SetDmBlockedRequest(platform, externalId, blocked),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>Ищет аккаунты по имени пользователя, ссылке или идентификатору.</summary>
    /// <param name="query">Поисковый запрос.</param>
    /// <param name="cancellationToken">Токен отмены запроса.</param>
    /// <returns>Найденные аккаунты и признак неоднозначности результата.</returns>
    public async Task<FindAccountsResponse> FindAccountsAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var url = $"api/accounts/find?query={Uri.EscapeDataString(query)}";
        using var response = await _http.GetAsync(url, cancellationToken);
        return await ReadAsync<FindAccountsResponse>(response, cancellationToken);
    }

    /// <summary>Возвращает подсказки аккаунтов для автодополнения команд.</summary>
    /// <param name="query">Введённый пользователем текст; пустая строка означает «недавние контакты».</param>
    /// <param name="limit">Максимальное количество подсказок.</param>
    /// <param name="cancellationToken">Токен отмены запроса.</param>
    /// <returns>Аккаунты, подходящие для подсказки, от наиболее подходящих к менее подходящим.</returns>
    public async Task<IReadOnlyList<AccountDto>> SuggestAccountsAsync(
        string? query,
        int limit = 25,
        CancellationToken cancellationToken = default)
    {
        var url = $"api/accounts/suggest?query={Uri.EscapeDataString(query ?? string.Empty)}&limit={limit}";
        using var response = await _http.GetAsync(url, cancellationToken);
        return await ReadAsync<AccountDto[]>(response, cancellationToken);
    }

    /// <summary>Загружает профиль аккаунта вместе со сведениями о связках.</summary>
    /// <param name="platform">Имя платформы.</param>
    /// <param name="externalId">Идентификатор пользователя внутри платформы.</param>
    /// <param name="cancellationToken">Токен отмены запроса.</param>
    /// <returns>Профиль аккаунта или <see langword="null"/>, если аккаунт не найден.</returns>
    public async Task<ProfileDto?> GetProfileAsync(
        string platform,
        string externalId,
        CancellationToken cancellationToken = default)
    {
        var url = $"api/accounts/profile?platform={Uri.EscapeDataString(platform)}&externalId={Uri.EscapeDataString(externalId)}";
        using var response = await _http.GetAsync(url, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        return await ReadAsync<ProfileDto>(response, cancellationToken);
    }

    /// <summary>Создаёт запрос на связку аккаунтов текущего пользователя с аккаунтом другого сервиса.</summary>
    /// <param name="sourcePlatform">Платформа инициатора.</param>
    /// <param name="sourceExternalId">Идентификатор инициатора на его платформе.</param>
    /// <param name="targetPlatform">Платформа целевого аккаунта или пустая строка для автоматического поиска.</param>
    /// <param name="targetQuery">Имя пользователя, ссылка или идентификатор целевого аккаунта.</param>
    /// <param name="cancellationToken">Токен отмены запроса.</param>
    /// <returns>Результат создания запроса: идентификатор, статус и сведения о получателе.</returns>
    public async Task<CreateLinkResult> CreateLinkRequestAsync(
        string sourcePlatform,
        string sourceExternalId,
        string targetPlatform,
        string targetQuery,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(
            "api/links/request",
            new CreateLinkRequest(sourcePlatform, sourceExternalId, targetPlatform, targetQuery),
            cancellationToken);

        return await ReadAsync<CreateLinkResult>(response, cancellationToken);
    }

    /// <summary>Подтверждает входящий запрос на связку.</summary>
    /// <param name="requestId">Идентификатор запроса.</param>
    /// <param name="responderPlatform">Платформа отвечающего.</param>
    /// <param name="responderExternalId">Идентификатор отвечающего на его платформе.</param>
    /// <param name="cancellationToken">Токен отмены запроса.</param>
    /// <returns>Результат обработки запроса.</returns>
    public async Task<RespondLinkResult> AcceptLinkAsync(
        Guid requestId,
        string responderPlatform,
        string responderExternalId,
        CancellationToken cancellationToken = default)
        => await RespondAsync("accept", requestId, responderPlatform, responderExternalId, cancellationToken);

    /// <summary>Отклоняет входящий запрос на связку.</summary>
    /// <param name="requestId">Идентификатор запроса.</param>
    /// <param name="responderPlatform">Платформа отвечающего.</param>
    /// <param name="responderExternalId">Идентификатор отвечающего на его платформе.</param>
    /// <param name="cancellationToken">Токен отмены запроса.</param>
    /// <returns>Результат обработки запроса.</returns>
    public async Task<RespondLinkResult> DeclineLinkAsync(
        Guid requestId,
        string responderPlatform,
        string responderExternalId,
        CancellationToken cancellationToken = default)
        => await RespondAsync("decline", requestId, responderPlatform, responderExternalId, cancellationToken);

    private async Task<RespondLinkResult> RespondAsync(
        string action,
        Guid requestId,
        string responderPlatform,
        string responderExternalId,
        CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(
            $"api/links/{requestId}/{action}",
            new RespondLinkRequest(responderPlatform, responderExternalId),
            cancellationToken);

        return await ReadAsync<RespondLinkResult>(response, cancellationToken);
    }

    /// <summary>Сообщает, что уведомление о запросе связки не удалось доставить получателю.</summary>
    /// <param name="requestId">Идентификатор запроса.</param>
    /// <param name="reason">Причина неудачной доставки.</param>
    /// <param name="cancellationToken">Токен отмены запроса.</param>
    /// <returns>Задача выполнения запроса.</returns>
    public async Task NotificationFailedAsync(
        Guid requestId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(
            $"api/links/{requestId}/notification-failed",
            new ReasonRequest(reason),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>Связывает аккаунты администраторской командой, без подтверждения получателя.</summary>
    /// <param name="sourcePlatform">Платформа инициатора.</param>
    /// <param name="sourceExternalId">Идентификатор инициатора на его платформе.</param>
    /// <param name="targetPlatform">Платформа целевого аккаунта или пустая строка для автоматического поиска.</param>
    /// <param name="targetQuery">Имя пользователя, ссылка или идентификатор целевого аккаунта.</param>
    /// <param name="cancellationToken">Токен отмены запроса.</param>
    /// <returns>Результат связывания.</returns>
    public async Task<RespondLinkResult> AdminLinkAsync(
        string sourcePlatform,
        string sourceExternalId,
        string targetPlatform,
        string targetQuery,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(
            "api/admin/link",
            new AdminLinkRequest(sourcePlatform, sourceExternalId, targetPlatform, targetQuery),
            cancellationToken);

        return await ReadAsync<RespondLinkResult>(response, cancellationToken);
    }

    /// <summary>Разрывает связку аккаунта с остальными аккаунтами группы.</summary>
    /// <param name="platform">Имя платформы.</param>
    /// <param name="externalId">Идентификатор пользователя внутри платформы.</param>
    /// <param name="cancellationToken">Токен отмены запроса.</param>
    /// <returns>Результат разрыва связки.</returns>
    public async Task<UnlinkResult> UnlinkAsync(
        string platform,
        string externalId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(
            "api/admin/unlink",
            new UnlinkRequest(platform, externalId),
            cancellationToken);

        return await ReadAsync<UnlinkResult>(response, cancellationToken);
    }

    private async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "IdentityService вернул {StatusCode} для {Url}: {Body}",
                (int)response.StatusCode,
                response.RequestMessage?.RequestUri,
                body);

            throw new IdentityServiceException(
                (int)response.StatusCode,
                $"Сервис идентификации вернул ошибку {(int)response.StatusCode}: {body}");
        }

        var result = await response.Content.ReadFromJsonAsync<T>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            cancellationToken);

        return result ?? throw new IdentityServiceException(
            (int)response.StatusCode,
            "Сервис идентификации вернул пустой ответ.");
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new IdentityServiceException(
            (int)response.StatusCode,
            $"Сервис идентификации вернул ошибку {(int)response.StatusCode}: {body}");
    }
}

/// <summary>Исключение, возникающее при ошибочном ответе IdentityService.</summary>
public sealed class IdentityServiceException(int statusCode, string message) : Exception(message)
{
    /// <summary>HTTP-код ответа службы идентификации.</summary>
    public int StatusCode { get; } = statusCode;
}
