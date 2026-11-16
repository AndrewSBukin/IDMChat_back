using IDMChat.DTO;
using IDMChat.Models;
using System.Text.Json;

namespace IDMChat.Utils
{
    /// <summary>
    /// Маппинг текстового типа клавиатуры (приходит от ИДМ) в KeyboardJson для хранения в БД
    /// и в KeyboardDto для отдачи клиентам.
    /// </summary>
    public static class BotKeyboards
    {
        public const string AcceptKeyboardType = "accept";

        /// <summary>
        /// Преобразует тип клавиатуры из ИДМ в готовый KeyboardJson для сообщения.
        /// keyboard="accept" -> две кнопки: 👍 (/accept_yes) и 👎 (/accept_no).
        /// Неизвестный тип игнорируется (null = без клавиатуры).
        /// </summary>
        public static string? BuildKeyboardJson(string? keyboardType)
        {
            if (string.IsNullOrWhiteSpace(keyboardType)) return null;

            return keyboardType.Trim().ToLowerInvariant() switch
            {
                AcceptKeyboardType => JsonSerializer.Serialize(new KeyboardData
                {
                    Type = AcceptKeyboardType,
                    Rows = new List<List<ButtonData>>
                    {
                        new()
                        {
                            new ButtonData { Text = "👍", Action = "command:/accept_yes" },
                            new ButtonData { Text = "👎", Action = "command:/accept_no" }
                        }
                    },
                    IsPersistent = false
                }),
                _ => null
            };
        }

        /// <summary>
        /// Разбирает KeyboardJson сообщения в DTO для клиента (snake_case).
        /// Битый/пустой JSON возвращает null - клиенту клавиатуру не отдаём.
        /// </summary>
        public static KeyboardDto? ToDto(string? keyboardJson)
        {
            if (string.IsNullOrWhiteSpace(keyboardJson)) return null;

            try
            {
                var data = JsonSerializer.Deserialize<KeyboardData>(keyboardJson);
                if (data == null) return null;

                return new KeyboardDto
                {
                    type = data.Type,
                    is_persistent = data.IsPersistent,
                    rows = data.Rows
                        .Select(row => row
                            .Select(b => new ButtonDto
                            {
                                text = b.Text,
                                command = ExtractCommand(b.Action),
                                url = b.Url
                            })
                            .ToList())
                        .ToList()
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Из Action вида "command:/accept_yes" достаёт текст команды "/accept_yes".</summary>
        private static string? ExtractCommand(string? action)
        {
            if (string.IsNullOrEmpty(action)) return null;

            const string prefix = "command:";
            return action.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? action.Substring(prefix.Length)
                : action;
        }
    }
}
