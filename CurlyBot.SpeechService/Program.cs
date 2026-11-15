using CurlyBot.Contracts.Messaging;
using CurlyBot.Messaging;
using CurlyBot.SpeechService.Handlers;
using CurlyBot.SpeechService.Options;
using CurlyBot.SpeechService.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SpeechOptions>(builder.Configuration.GetSection("Speech"));
builder.Services.AddRabbitMqMessaging(builder.Configuration);

var speech = builder.Configuration.GetSection("Speech").Get<SpeechOptions>() ?? new SpeechOptions();
builder.Services.AddHttpClient("tts-engine", client =>
{
    client.BaseAddress = new Uri(speech.EngineUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(speech.TimeoutSeconds);
});

builder.Services.AddSingleton<SpeechSynthesisService>();
builder.Services.AddRabbitMqConsumer<SpeechRequested, SpeechRequestHandler>(
    RabbitTopology.SpeechRequestsQueue,
    RabbitTopology.SpeechRequestRoutingKey);

var app = builder.Build();

Directory.CreateDirectory(Path.GetFullPath(speech.OutputPath));

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "speech",
    engine = speech.EngineUrl,
    output = Path.GetFullPath(speech.OutputPath)
}));

app.Run();
