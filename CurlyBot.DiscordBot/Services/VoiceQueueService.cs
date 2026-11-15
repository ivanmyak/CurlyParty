using System.Collections.Concurrent;
using System.Diagnostics;
using CurlyBot.Clients;
using CurlyBot.Contracts;
using CurlyBot.DiscordBot.Classes;
using CurlyBot.DiscordBot.Interfaces;
using Discord;
using Discord.Audio;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CurlyBot.DiscordBot.Services
{
    internal class VoiceQueueService : IVoiceQueue
    {
        private readonly SpeechSynthesisClient _speech;
        private readonly IdentityServiceClient _identity;
        private readonly ITranslate _translator;
        private readonly IVoiceTemplate _templates;
        private readonly DiscordSocketClient _client;
        private readonly ILogger<VoiceQueueService> _logger;

        private readonly ConcurrentQueue<VoiceQueueItem> _queue = new();
        private readonly SemaphoreSlim _signal = new(0);

        private readonly ConcurrentDictionary<ulong, (IAudioClient Client, ulong ChannelId)> _connections = new();

        private readonly int _delayBetweenMessagesMs;

        public VoiceQueueService(
            IConfiguration config,
            SpeechSynthesisClient speech,
            IdentityServiceClient identity,
            DiscordSocketClient client,
            ITranslate translator,
            IVoiceTemplate templates,
            ILogger<VoiceQueueService> logger)
        {
            _speech = speech;
            _identity = identity;
            _client = client;
            _translator = translator;
            _templates = templates;
            _logger = logger;

            var debounceDelayMillisecondsStr = config["VoiceSettings:DebounceDelayMilliseconds"];

            if (!int.TryParse(debounceDelayMillisecondsStr, out _delayBetweenMessagesMs) || _delayBetweenMessagesMs <= 0)
            {
                _delayBetweenMessagesMs = 200;
                _logger.LogWarning("[VOICEINIT] VoiceSettings:DebounceDelayMilliseconds отсутствует или некорректен. Используем значение по умолчанию: 200");
            }
            else
            {
                _logger.LogInformation("[VOICEINIT] DebounceDelayMilliseconds загружен из конфигурации: {Value}", _delayBetweenMessagesMs);
            }

            Task.Run(ProcessQueueAsync);
        }

        // ================================
        //  ПРИВЕТСТВИЯ
        // ================================
        public async Task EnqueueJoinAsync(IVoiceChannel channel, string userName, string activity)
        {
            _logger.LogInformation("[VOICEJOIN] EnqueueJoinAsync: channel={Channel}, user={User}, activity={Activity}",
                channel.Name, userName, activity ?? "<null>");

            string text;

            if (string.IsNullOrEmpty(activity))
            {
                text = _templates.FormatSingle(userName);
            }
            else
            {
                var ruAct = await _translator.GetRussianActivityAsync(activity);
                text = _templates.FormatSingleWithActivity(userName, ruAct);
            }

            _queue.Enqueue(new VoiceQueueItem
            {
                Channel = channel,
                Text = text
            });

            _logger.LogInformation("[VOICEJOIN] В очередь добавлен Join: Channel={Channel}, Text=\"{Text}\"",
                channel.Name, text);

            _signal.Release();
        }

        // ================================
        //  /TALK и DM
        // ================================
        public Task EnqueueDirectMessageAsync(
            SocketUser sender,
            string text,
            bool forceDM = false,
            SocketUser? recipient = null)
        {
            _logger.LogInformation(
                "[VOICEDMQUEUE] sender={Sender}({SenderId}), recipient={Recipient}, forceDM={Force}, text=\"{Text}\"",
                sender.Username, sender.Id,
                recipient?.Username ?? "<null>",
                forceDM,
                text
            );

            // recipient не указан — отправляем самому себе
            recipient ??= sender;
            bool isSelf = recipient.Id == sender.Id;

            // === 1. Попытка в голосовой канал (если не forceDM) ===
            if (!forceDM && recipient is SocketGuildUser { VoiceChannel: not null } recipientGuildUser)
            {
                _queue.Enqueue(new VoiceQueueItem
                {
                    Channel = recipientGuildUser.VoiceChannel,
                    Text = text,
                    Sender = sender,
                    Recipient = recipient
                });

                // DM отправителю (копия)
                _queue.Enqueue(new VoiceQueueItem
                {
                    DmUser = sender,
                    Text = text,
                    ForceDM = true,
                    Sender = sender,
                    Recipient = recipient
                });

                _signal.Release(2);
                return Task.CompletedTask;
            }

            // === 2. Режим DM (forceDM или нет голосового канала) ===
            if (isSelf)
            {
                _queue.Enqueue(new VoiceQueueItem
                {
                    DmUser = sender,
                    Text = text,
                    ForceDM = true,
                    Sender = sender,
                    Recipient = recipient
                });

                _signal.Release(1);
                return Task.CompletedTask;
            }

            // Не self → два DM: получателю и отправителю
            _queue.Enqueue(new VoiceQueueItem
            {
                DmUser = recipient,
                Text = text,
                ForceDM = true,
                Sender = sender,
                Recipient = recipient
            });

            _queue.Enqueue(new VoiceQueueItem
            {
                DmUser = sender,
                Text = text,
                ForceDM = true,
                Sender = sender,
                Recipient = recipient
            });

            _signal.Release(2);
            return Task.CompletedTask;
        }

        // ================================
        //  ОСНОВНОЙ ОБРАБОТЧИК ОЧЕРЕДИ
        // ================================
        private async Task ProcessQueueAsync()
        {
            _logger.LogInformation("[VOICEPROCQUEUE] Обработчик очереди запущен");

            while (true)
            {
                await _signal.WaitAsync();

                if (!_queue.TryDequeue(out var item))
                    continue;

                _logger.LogInformation(
                    "[VOICEPROCQUEUE] Извлечена задача: Text=\"{Text}\", Channel={Channel}, DM={Dm}, ForceDM={ForceDM}, Sender={Sender}, Recipient={Recipient}",
                    item.Text,
                    item.Channel?.Name ?? "<null>",
                    item.DmUser?.Username ?? "<null>",
                    item.ForceDM,
                    item.Sender?.Username ?? "<null>",
                    item.Recipient?.Username ?? "<null>"
                );

                try
                {
                    if (string.IsNullOrWhiteSpace(item.Text))
                    {
                        _logger.LogInformation("[VOICEPROCQUEUE] Пустой текст в задаче — пропускаем");
                        continue;
                    }

                    if (item.ForceDM && item.DmUser != null)
                    {
                        await HandleDmItemAsync(item);
                        continue;
                    }

                    if (item.Channel == null && item.DmUser != null)
                    {
                        await HandleDmItemAsync(item);
                        continue;
                    }

                    if (item.Channel == null)
                    {
                        _logger.LogWarning("[VOICEPROCQUEUE] Задача без канала и без DM-пользователя — пропускаем");
                        continue;
                    }

                    if (!ChannelHasRealUsers(item.Channel))
                    {
                        _logger.LogInformation("[VOICEPROCQUEUE] Канал {Channel} пуст, пропускаем: {Text}",
                            item.Channel.Name, item.Text);
                        continue;
                    }

                    await PlayToVoiceChannelAsync(item);

                    await Task.Delay(_delayBetweenMessagesMs);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[VOICEPROCQUEUE] Ошибка при обработке очереди");
                }

                if (_queue.IsEmpty)
                {
                    _logger.LogInformation("[VOICEPROCQUEUE] Очередь пуста — закрываем все голосовые подключения");

                    foreach (var kv in _connections)
                    {
                        try
                        {
                            _logger.LogInformation("[VOICEPROCQUEUE] Отключаемся от GuildId={GuildId}", kv.Key);
                            await kv.Value.Client.StopAsync();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[VOICEPROCQUEUE] Ошибка при отключении от GuildId={GuildId}", kv.Key);
                        }

                        _connections.TryRemove(kv.Key, out _);
                    }
                }
            }
        }

        // ================================
        //  DM-ОТПРАВКА
        // ================================
        private async Task HandleDmItemAsync(VoiceQueueItem item)
        {
            var dmUser = item.DmUser;
            if (dmUser is null)
            {
                _logger.LogWarning("[VOICEDMHANDLER] Задача без DM-пользователя — пропускаем");
                return;
            }

            _logger.LogInformation("[VOICEDMHANDLER] DM → {User}", dmUser.Username);

            var speech = await _speech.SynthesizeAsync(item.Text);

            try
            {
                bool isSelf = item.Sender != null &&
                              item.Recipient != null &&
                              item.Sender.Id == item.Recipient.Id &&
                              dmUser.Id == item.Sender.Id;

                if (isSelf)
                {
                    await dmUser.SendMessageAsync(_templates.FormatTalkNotifySelf());
                    await dmUser.SendFileAsync(speech.WavPath);
                    await TouchContactSafeAsync(dmUser);
                    return;
                }

                if (dmUser.Id == item.Sender?.Id && item.Recipient != null)
                {
                    await dmUser.SendMessageAsync(
                        _templates.FormatTalkNotifySender(item.Recipient)
                    );
                    await dmUser.SendFileAsync(speech.WavPath);
                    await TouchContactSafeAsync(dmUser);
                    return;
                }

                if (dmUser.Id == item.Recipient?.Id && item.Sender != null)
                {
                    await dmUser.SendMessageAsync(
                        _templates.FormatTalkNotifyRecipient(item.Sender)
                    );
                    await dmUser.SendFileAsync(speech.WavPath);
                    await TouchContactSafeAsync(dmUser);
                    return;
                }

                await dmUser.SendFileAsync(speech.WavPath, "Вот ваша озвучка:");
                await TouchContactSafeAsync(dmUser);
            }
            catch (Discord.Net.HttpException ex) when (ex.DiscordCode == DiscordErrorCode.CannotSendMessageToUser)
            {
                _logger.LogWarning("[VOICEDMHANDLER] У пользователя {User} закрыты ЛС", dmUser.Username);
                await MarkDmBlockedSafeAsync(dmUser.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VOICEDMHANDLER] Ошибка отправки DM пользователю {User}", dmUser.Username);

                try
                {
                    await dmUser.SendMessageAsync("Не удалось отправить аудиофайл.");
                }
                catch (Exception ex2)
                {
                    _logger.LogWarning(ex2, "[VOICEDMHANDLER] Ошибка отправки fallback-сообщения");
                }
            }
        }

        // ================================
        //  ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ
        // ================================
        private bool ChannelHasRealUsers(IVoiceChannel channel)
        {
            if (channel is not SocketVoiceChannel vc)
                return true;

            var has = vc.Users.Any(u => !u.IsBot);
            _logger.LogInformation("[VOICECHANNELNONEMPTY] ChannelHasRealUsers: Channel={Channel}, HasRealUsers={Has}",
                vc.Name, has);
            return has;
        }

        private async Task<IAudioClient> GetOrConnectAsync(IVoiceChannel channel)
        {
            if (_connections.TryGetValue(channel.GuildId, out var existing))
            {
                if (existing.Client.ConnectionState == ConnectionState.Connected &&
                    existing.ChannelId == channel.Id)
                {
                    return existing.Client;
                }

                try { await existing.Client.StopAsync(); } catch { }
                _connections.TryRemove(channel.GuildId, out _);
            }

            var client = await channel.ConnectAsync(selfDeaf: true);
            _connections[channel.GuildId] = (client, channel.Id);
            return client;
        }

        private async Task PlayToVoiceChannelAsync(VoiceQueueItem item)
        {
            var audioClient = await GetOrConnectAsync(item.Channel!);

            var speech = await _speech.SynthesizeAsync(item.Text);

            using var ffmpeg = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-hide_banner -loglevel panic -i \"{speech.WavPath}\" -ac 2 -f s16le -ar 48000 pipe:1",
                UseShellExecute = false,
                RedirectStandardOutput = true
            }) ?? throw new InvalidOperationException("Не удалось запустить ffmpeg");

            using var output = ffmpeg.StandardOutput.BaseStream;
            using var discord = audioClient.CreatePCMStream(AudioApplication.Mixed, bufferMillis: 1);

            try
            {
                await output.CopyToAsync(discord);
            }
            finally
            {
                await discord.FlushAsync();
            }

            if (item.Channel is { } channel && !ChannelHasRealUsers(channel))
            {
                await audioClient.StopAsync();
                _connections.TryRemove(item.Channel.GuildId, out _);
            }
        }

        private async Task TouchContactSafeAsync(SocketUser user)
        {
            try
            {
                await _identity.TouchContactAsync(Platforms.Discord, user.Id.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Не удалось обновить контакт {User} в IdentityService", user.Id);
            }
        }

        private async Task MarkDmBlockedSafeAsync(ulong userId)
        {
            try
            {
                await _identity.SetDmBlockedAsync(Platforms.Discord, userId.ToString(), true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Не удалось отметить закрытые ЛС для {User}", userId);
            }
        }
    }
}
