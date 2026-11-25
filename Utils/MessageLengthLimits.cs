using Microsoft.Extensions.Configuration;

namespace IDMChat.Utils
{
    /// <summary>
    /// Централизованные лимиты длины сообщений (читаются из appsettings.json).
    /// </summary>
    public static class MessageLengthLimits
    {
        public const int DefaultUserMaxLength = 4096;
        public const int DefaultSystemMaxLength = 30000;

        public static int GetUserMaxLength(IConfiguration config)
            => GetInt(config, "Messages:MaxTextLength", DefaultUserMaxLength);

        public static int GetSystemMaxLength(IConfiguration config)
            => GetInt(config, "Messages:SystemMaxTextLength", DefaultSystemMaxLength);

        public static bool IsTooLong(string? text, int maxLength)
            => text != null && text.Length > maxLength;

        private static int GetInt(IConfiguration config, string key, int fallback)
            => int.TryParse(config[key], out var value) && value > 0 ? value : fallback;
    }
}
