using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace CurlyBot.DiscordBot.Services
{
    /// <summary>Хранит в файле настройки автоматического перемещения пользователей между каналами.</summary>
    public static class IgnoreManager
    {
        private static readonly string FilePath = Path.Combine(Directory.GetCurrentDirectory(), "Data", "tracking.json");

        private static Dictionary<ulong, bool> tracking = new();
        private static readonly object _lock = new();

        static IgnoreManager()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    tracking = JsonSerializer.Deserialize<Dictionary<ulong, bool>>(json) ?? new();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка загрузки tracking.json: {ex.Message}");
                tracking = new();
            }
        }

        /// <summary>Проверяет, отключено ли отслеживание пользователя.</summary>
        /// <param name="userId">Идентификатор пользователя Discord.</param>
        /// <returns><see langword="true"/>, если пользователь игнорируется.</returns>
        public static bool IsIgnored(ulong userId)
        {
            if (!tracking.ContainsKey(userId)) return false; // по умолчанию отслеживаем
            return !tracking[userId];
        }

        /// <summary>Добавляет пользователя в файл настроек с включённым отслеживанием.</summary>
        /// <param name="userId">Идентификатор пользователя Discord.</param>
        public static void EnsureUser(ulong userId)
        {
            lock (_lock)
            {
                if (!tracking.ContainsKey(userId))
                {
                    tracking[userId] = true;
                    Save();
                }
            }
        }

        /// <summary>Включает или отключает отслеживание пользователя и сохраняет файл.</summary>
        /// <param name="userId">Идентификатор пользователя Discord.</param>
        /// <param name="track">Новое значение признака отслеживания.</param>
        public static void SetTracking(ulong userId, bool track)
        {
            lock (_lock)
            {
                tracking[userId] = track;
                Save();
            }
        }

        /// <summary>Удаляет пользователя из файла настроек.</summary>
        /// <param name="userId">Идентификатор пользователя Discord.</param>
        public static void RemoveUser(ulong userId)
        {
            lock (_lock)
            {
                tracking.Remove(userId);
                Save();
            }
        }

        /// <summary>Возвращает идентификаторы пользователей с отключённым отслеживанием.</summary>
        /// <returns>Последовательность идентификаторов игнорируемых пользователей.</returns>
        public static IEnumerable<ulong> GetIgnoredUsers()
            => tracking.Where(kvp => !kvp.Value).Select(kvp => kvp.Key);

        private static void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(tracking, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка сохранения tracking.json: {ex.Message}");
            }
        }
    }

}
