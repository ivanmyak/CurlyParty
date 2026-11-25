using System.Reflection;
using CurlyBot.Clients;
using CurlyBot.Contracts;
using CurlyBot.Contracts.Messaging;
using CurlyBot.Messaging;
using CurlyBot.TelegramBot.Options;
using CurlyBot.TelegramBot.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Telegram.Bot;

var builder = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((context, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: true);
        config.AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true);
        config.AddEnvironmentVariables();
    })
    .ConfigureServices((context, services) =>
    {
        services.Configure<TelegramOptions>(context.Configuration.GetSection("Telegram"));
        services.PostConfigure<TelegramOptions>(options =>
        {
            // Поддержка старого имени переменной из env.env.
            var legacyToken = context.Configuration["TelegramToken"];
            if (!string.IsNullOrWhiteSpace(legacyToken))
                options.Token = legacyToken;
        });

        services.AddSingleton<ITelegramBotClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<TelegramOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.Token))
                throw new InvalidOperationException(
                    "Telegram:Token не настроен (env: Telegram__Token).");

            return new TelegramBotClient(options.Token);
        });

        services.AddRabbitMqMessaging(context.Configuration);
        services.AddIdentityServiceClient(context.Configuration);
        services.AddSpeechSynthesisClient(
            context.Configuration,
            RabbitTopology.SpeechRepliesTelegramQueue);

        services.AddRabbitMqConsumer<LinkConfirmationRequested, TelegramLinkEventsHandler>(
            RabbitTopology.LinkConfirmationQueue(Platforms.Telegram),
            RabbitTopology.LinkConfirmationRoutingKey(Platforms.Telegram));

        services.AddRabbitMqConsumer<LinkResultNotification, TelegramLinkEventsHandler>(
            RabbitTopology.LinkResultQueue(Platforms.Telegram),
            RabbitTopology.LinkResultRoutingKey(Platforms.Telegram));

        services.AddRabbitMqConsumer<SpeechDeliveryRequested, TelegramLinkEventsHandler>(
            RabbitTopology.DeliveryQueue(Platforms.Telegram),
            RabbitTopology.DeliveryRoutingKey(Platforms.Telegram));

        services.AddSingleton<TalkDialogState>();
        services.AddScoped<TelegramSender>();
        services.AddScoped<TelegramCommandHandler>();
        services.AddScoped<TelegramTalkDialogHandler>();
        services.AddScoped<TelegramCallbackHandler>();
        services.AddHostedService<TelegramBotService>();
    });

using var host = builder.Build();
await host.RunAsync();
