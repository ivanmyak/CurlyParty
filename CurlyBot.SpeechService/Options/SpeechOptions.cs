namespace CurlyBot.SpeechService.Options;

/// <summary>Настройки сервиса синтеза речи.</summary>
public sealed class SpeechOptions
{
    /// <summary>Базовый адрес piper-http.</summary>
    public string EngineUrl { get; set; } = "http://localhost:5000";

    /// <summary>Каталог кэша готовых аудиофайлов.</summary>
    public string OutputPath { get; set; } = "data/tts";

    /// <summary>Имя используемой голосовой модели.</summary>
    public string VoiceModel { get; set; } = "ru_RU-denis-medium.onnx";

    /// <summary>Путь к исполняемому файлу ffmpeg.</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>Таймаут запроса к piper-http в секундах.</summary>
    public int TimeoutSeconds { get; set; } = 180;
}
