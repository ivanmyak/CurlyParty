using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CurlyBot.SpeechService.Options;
using Microsoft.Extensions.Options;

namespace CurlyBot.SpeechService.Services;

/// <summary>Результат синтеза речи: пути к файлам и размер аудио.</summary>
/// <param name="WavPath">Путь к WAV-файлу.</param>
/// <param name="OggPath">Путь к OGG-файлу, если конвертация удалась.</param>
/// <param name="SizeBytes">Размер WAV-файла в байтах.</param>
public sealed record SynthesizedAudio(string WavPath, string? OggPath, long SizeBytes);

/// <summary>
///     Вызывает piper-http, кэширует WAV по хэшу текста и дополнительно
///     конвертирует его в OGG/Opus для Telegram voice-сообщений.
/// </summary>
public sealed class SpeechSynthesisService(
    IHttpClientFactory httpClientFactory,
    IOptions<SpeechOptions> options,
    ILogger<SpeechSynthesisService> logger)
{
    private readonly SpeechOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Синтезирует речь, используя кэш по хэшу текста.</summary>
    /// <param name="text">Текст для озвучивания.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Пути к готовым файлам и размер аудио.</returns>
    /// <exception cref="ArgumentException">Текст пуст.</exception>
    public async Task<SynthesizedAudio> SynthesizeAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Текст для озвучки пуст.", nameof(text));

        var outputPath = Path.GetFullPath(_options.OutputPath);
        Directory.CreateDirectory(outputPath);

        var hash = GetHash(text);
        var wavPath = Path.Combine(outputPath, $"{hash}.wav");
        var oggPath = Path.Combine(outputPath, $"{hash}.ogg");

        if (File.Exists(wavPath) && File.Exists(oggPath))
            return await BuildResultAsync(wavPath, oggPath, cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(wavPath))
                await DownloadWavAsync(text, wavPath, cancellationToken);

            if (!File.Exists(oggPath))
                await TryConvertToOggAsync(wavPath, oggPath, cancellationToken);

            return await BuildResultAsync(wavPath, File.Exists(oggPath) ? oggPath : null, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DownloadWavAsync(string text, string wavPath, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("tts-engine");
        var url = $"/?text={Uri.EscapeDataString(text)}";
        var tempPath = $"{wavPath}.{Guid.NewGuid():N}.tmp";

        logger.LogInformation("TTS: генерируем новый файл {WavPath}", wavPath);

        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using (var network = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var file = File.Create(tempPath))
            {
                await network.CopyToAsync(file, cancellationToken);
            }

            File.Move(tempPath, wavPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private async Task TryConvertToOggAsync(string wavPath, string oggPath, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _options.FfmpegPath,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(wavPath);
            startInfo.ArgumentList.Add("-c:a");
            startInfo.ArgumentList.Add("libopus");
            startInfo.ArgumentList.Add("-b:a");
            startInfo.ArgumentList.Add("48k");
            startInfo.ArgumentList.Add(oggPath);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Не удалось запустить ffmpeg.");

            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                logger.LogWarning("TTS: ffmpeg завершился с кодом {Code}: {Error}", process.ExitCode, stderr);
                TryDelete(oggPath);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TTS: не удалось конвертировать WAV в OGG. Telegram получит WAV.");
            TryDelete(oggPath);
        }
    }

    private static async Task<SynthesizedAudio> BuildResultAsync(
        string wavPath,
        string? oggPath,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(wavPath);
        return await Task.FromResult(new SynthesizedAudio(wavPath, oggPath, info.Length));
    }

    private string GetHash(string text)
    {
        var bytes = Encoding.UTF8.GetBytes($"{_options.VoiceModel}|{text}");
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignored
        }
    }
}
