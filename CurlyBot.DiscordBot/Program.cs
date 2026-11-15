using CurlyBot.Clients;
using CurlyBot.Contracts;
using CurlyBot.Contracts.Messaging;
using CurlyBot.DiscordBot.Interfaces;
using CurlyBot.DiscordBot.Services;
using CurlyBot.Messaging;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Reflection;
using System.Runtime.InteropServices;

internal class Program
{
    private static async Task Main(string[] args)
    {
        Console.WriteLine("=== ЗАПУСК СИСТЕМЫ ЗАГРУЗКИ БИБЛИОТЕК ===");

        string[] libs = { "libdave.so" };
        foreach (var lib in libs)
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, lib);
                if (!File.Exists(path))
                    path = Path.Combine(AppContext.BaseDirectory, "libs", lib);
                if (!File.Exists(path))
                    path = Path.Combine("/app", lib);

                var handle = NativeLibrary.Load(path);
                if (handle != IntPtr.Zero)
                    Console.WriteLine($"=== УСПЕХ: {lib} загружена! ===");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"=== ПРОВАЛ: {lib} не загружена: {ex.Message} ===");
            }
        }

        using IHost host = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((context, config) =>
            {
                config.AddJsonFile("appsettings.json", optional: true);
                config.AddUserSecrets(Assembly.GetExecutingAssembly());
                config.AddEnvironmentVariables();
            })
            .ConfigureServices((context, services) =>
            {
                var socketConfig = new DiscordSocketConfig
                {
                    EnableVoiceDaveEncryption = true,
                    GatewayIntents = GatewayIntents.Guilds |
                                     GatewayIntents.GuildMembers |
                                     GatewayIntents.GuildVoiceStates |
                                     GatewayIntents.GuildPresences
                };

                services.AddSingleton(socketConfig);
                services.AddSingleton<DiscordSocketClient>();
                services.AddSingleton(x => new InteractionService(x.GetRequiredService<DiscordSocketClient>()));

                services.AddRabbitMqMessaging(context.Configuration);
                services.AddIdentityServiceClient(context.Configuration);
                services.AddSpeechSynthesisClient(
                    context.Configuration,
                    RabbitTopology.SpeechRepliesDiscordQueue);

                services.AddRabbitMqConsumer<LinkConfirmationRequested, DiscordLinkEventsHandler>(
                    RabbitTopology.LinkConfirmationQueue(Platforms.Discord),
                    RabbitTopology.LinkConfirmationRoutingKey(Platforms.Discord));

                services.AddRabbitMqConsumer<LinkResultNotification, DiscordLinkEventsHandler>(
                    RabbitTopology.LinkResultQueue(Platforms.Discord),
                    RabbitTopology.LinkResultRoutingKey(Platforms.Discord));

                services.AddRabbitMqConsumer<SpeechDeliveryRequested, DiscordLinkEventsHandler>(
                    RabbitTopology.DeliveryQueue(Platforms.Discord),
                    RabbitTopology.DeliveryRoutingKey(Platforms.Discord));

                services.AddSingleton<MoveUserManager>();
                services.AddSingleton<LoggingService>();
                services.AddSingleton<LinkInteractionHandler>();

                services.AddSingleton<ITranslate, TranslationService>();
                services.AddSingleton<IVoiceQueue, VoiceQueueService>();
                services.AddSingleton<IVoiceTemplate, VoiceTemplateService>();
            })
            .Build();

        await RunBotAsync(host);
    }

    private static async Task RunBotAsync(IHost host)
    {
        var currentLdPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "";
        Environment.SetEnvironmentVariable("LD_LIBRARY_PATH", $"/app:{currentLdPath}");

        var client = host.Services.GetRequiredService<DiscordSocketClient>();
        var interactions = host.Services.GetRequiredService<InteractionService>();
        var config = host.Services.GetRequiredService<IConfiguration>();
        var linkHandler = host.Services.GetRequiredService<LinkInteractionHandler>();

        host.Services.GetRequiredService<MoveUserManager>();
        host.Services.GetRequiredService<LoggingService>();

        client.Log += (msg) =>
        {
            if (msg.Exception is Discord.Net.WebSocketClosedException wsEx && wsEx.CloseCode == 4017)
            {
                Console.WriteLine("КРИТИЧЕСКАЯ ОШИБКА: Discord требует шифрование DAVE. Проверь наличие libdave.so в /usr/lib/");
            }
            Console.WriteLine(msg.ToString());
            return Task.CompletedTask;
        };

        client.Ready += async () =>
        {
            await interactions.AddModulesAsync(Assembly.GetEntryAssembly(), host.Services);
            await interactions.RegisterCommandsGloballyAsync();
        };

        client.InteractionCreated += async (interaction) =>
        {
            if (interaction is SocketMessageComponent component &&
                component.Data.CustomId.StartsWith("link:", StringComparison.Ordinal))
            {
                await linkHandler.HandleAsync(component);
                return;
            }

            var ctx = new SocketInteractionContext(client, interaction);
            await interactions.ExecuteCommandAsync(ctx, host.Services);
        };

        string token = config["DiscordToken"] ?? throw new Exception("Missing Discord Token!");
        await client.LoginAsync(TokenType.Bot, token);
        await client.StartAsync();
        await host.RunAsync();
    }
}
