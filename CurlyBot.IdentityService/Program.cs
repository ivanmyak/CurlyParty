using CurlyBot.Contracts.Identity;
using CurlyBot.IdentityService.Data;
using CurlyBot.IdentityService.Options;
using CurlyBot.IdentityService.Services;
using CurlyBot.Messaging;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<IdentityLinkOptions>(builder.Configuration.GetSection("Link"));

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException(
        "Не задана строка подключения ConnectionStrings:Postgres (env: ConnectionStrings__Postgres).");

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<IdentityStore>();
builder.Services.AddSingleton<LinkService>();
builder.Services.AddRabbitMqMessaging(builder.Configuration);

var app = builder.Build();

await InitializeDatabaseAsync(app.Services, app.Logger);

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "identity" }));

app.MapPost("/api/accounts/upsert", async (
    UpsertAccountRequest request,
    IdentityStore store,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.ExternalId))
        return Results.BadRequest(new { error = "externalId обязателен" });

    var account = await store.UpsertAccountAsync(
        request.Platform,
        request.ExternalId,
        request.Username,
        request.DisplayName,
        cancellationToken);

    return Results.Ok(account.ToDto());
});

app.MapPost("/api/accounts/touch", async (
    TouchContactRequest request,
    IdentityStore store,
    CancellationToken cancellationToken) =>
{
    await store.TouchContactAsync(request.Platform, request.ExternalId, cancellationToken);
    return Results.NoContent();
});

app.MapPost("/api/accounts/blocked", async (
    SetDmBlockedRequest request,
    IdentityStore store,
    CancellationToken cancellationToken) =>
{
    await store.SetDmBlockedAsync(request.Platform, request.ExternalId, request.Blocked, cancellationToken);
    return Results.NoContent();
});

app.MapGet("/api/accounts/suggest", async (
    string? query,
    int? limit,
    IdentityStore store,
    CancellationToken cancellationToken) =>
{
    var accounts = await store.SuggestAccountsAsync(query, limit ?? 25, cancellationToken);
    return Results.Ok(accounts.Select(x => x.ToDto()).ToArray());
});

app.MapGet("/api/accounts/find", async (
    string query,
    LinkService links,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(query))
        return Results.Ok(new FindAccountsResponse(Array.Empty<AccountDto>(), false));

    return Results.Ok(await links.FindAsync(query, cancellationToken));
});

app.MapGet("/api/accounts/profile", async (
    string platform,
    string externalId,
    IdentityStore store,
    CancellationToken cancellationToken) =>
{
    var profile = await store.GetProfileAsync(platform, externalId, cancellationToken);
    return profile is null ? Results.NotFound() : Results.Ok(profile);
});

app.MapPost("/api/links/request", async (
    CreateLinkRequest request,
    LinkService links,
    CancellationToken cancellationToken) =>
    Results.Ok(await links.CreateRequestAsync(request, cancellationToken)));

app.MapPost("/api/links/{requestId:guid}/accept", async (
    Guid requestId,
    RespondLinkRequest request,
    LinkService links,
    CancellationToken cancellationToken) =>
    Results.Ok(await links.AcceptAsync(requestId, request, cancellationToken)));

app.MapPost("/api/links/{requestId:guid}/decline", async (
    Guid requestId,
    RespondLinkRequest request,
    LinkService links,
    CancellationToken cancellationToken) =>
    Results.Ok(await links.DeclineAsync(requestId, request, cancellationToken)));

app.MapPost("/api/links/{requestId:guid}/notification-failed", async (
    Guid requestId,
    ReasonRequest request,
    LinkService links,
    CancellationToken cancellationToken) =>
{
    await links.NotificationFailedAsync(
        requestId,
        string.IsNullOrWhiteSpace(request.Reason) ? "unknown" : request.Reason,
        cancellationToken);
    return Results.NoContent();
});

app.MapPost("/api/admin/link", async (
    AdminLinkRequest request,
    LinkService links,
    CancellationToken cancellationToken) =>
    Results.Ok(await links.AdminLinkAsync(request, cancellationToken)));

app.MapPost("/api/admin/unlink", async (
    UnlinkRequest request,
    LinkService links,
    CancellationToken cancellationToken) =>
    Results.Ok(await links.UnlinkAsync(request, cancellationToken)));

app.Run();

static async Task InitializeDatabaseAsync(IServiceProvider services, ILogger logger)
{
    var store = services.GetRequiredService<IdentityStore>();

    for (var attempt = 1; attempt <= 30; attempt++)
    {
        try
        {
            await store.InitializeAsync(CancellationToken.None);
            return;
        }
        catch (Exception ex) when (attempt < 30)
        {
            logger.LogWarning(
                ex,
                "Postgres ещё не готов (попытка {Attempt}/30). Повтор через 2 сек.",
                attempt);
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    await store.InitializeAsync(CancellationToken.None);
}
