namespace IDMChat.Services
{
    public interface IChatPathUrlResolver
    {
        string? ResolveUrl(string? relativePath);
        string? ResolveAvatarThumbUrl(string? relativePath);
    }

    public class ChatPathUrlResolver : IChatPathUrlResolver
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly string _fallbackBaseUrl;

        public ChatPathUrlResolver(IHttpContextAccessor httpContextAccessor, IConfiguration configuration)
        {
            _httpContextAccessor = httpContextAccessor;
            // Дефолтный URL из appsettings.json на случай, если запрос идет вне HTTP-контекста
            _fallbackBaseUrl = configuration["AppSettings:AppBaseUrl"] ?? "http://localhost:5000";
        }

        public string? ResolveUrl(string? relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return null;

            // --- НАЧАЛО БЛОКА ЗАЩИТЫ И АВТОНОМНОЙ ФИЛЬТРАЦИИ ---
            // 1. Защита: если это уже готовая ссылка, отдаем её без изменений
            if (relativePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return relativePath;
            }

            // Унифицируем разделители пути (заменяем Windows бэкслеши на веб-слэши)
            var cleanPath = relativePath.Replace('\\', '/');

            // 2. Автономное вычисление относительного пути
            // Список ключевых папок, с которых обычно начинаются относительные пути в вашем чате
            var storageFolders = new[] { "avatars/", "attachments/", "thumbnails/", "files/" };

            foreach (var folder in storageFolders)
            {
                // Ищем, где в абсолютном пути (например, "C:/app/www/storage/avatars/abc.jpg") начинается наша папка
                int index = cleanPath.IndexOf(folder, StringComparison.OrdinalIgnoreCase);
                if (index != -1)
                {
                    // Отрезаем всё, что шло ДО этой папки (останется строго: "avatars/abc.jpg")
                    cleanPath = cleanPath.Substring(index);
                    break; // Папка найдена, выходим из цикла
                }
            }

            // Если путь всё еще выглядит как абсолютный путь диска Windows (например, "C:/...") или Linux ("/var/..."),
            // и он не совпал ни с одной папкой из списка, забираем только имя файла во избежание поломки URL
            if (Path.IsPathRooted(relativePath) && (cleanPath.Contains(":/") || cleanPath.StartsWith("/")))
            {
                cleanPath = Path.GetFileName(cleanPath);
            }

            // Убираем лишние ведущие слэши
            cleanPath = cleanPath.TrimStart('/');
            // --- КОНЕЦ БЛОКА ФИЛЬТРАЦИИ ---

            // 3. Сборка базового домена
            var context = _httpContextAccessor.HttpContext;
            string baseUrl;

            if (context != null && !string.IsNullOrEmpty(context.Request.Host.Value))
            {
                baseUrl = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";
            }
            else
            {
                baseUrl = _fallbackBaseUrl.TrimEnd('/');
            }

            // Возвращаем идеальный, чистый веб-URL для фронтенда
            return $"{baseUrl}/api/files/{cleanPath}";
        }



        public string? ResolveAvatarThumbUrl(string? relativePath)
        {
            return ResolveUrl(relativePath);
        }
    }
    public static class UrlResolverExtensions
    {
        public static string? ResolveAvatarThumbUrl(this ChatPathUrlResolver urlResolver, string? storagePath)
        {
            if (string.IsNullOrEmpty(storagePath)) return null;

            // Находим расширение файла (например, .jpg)
            var extension = Path.GetExtension(storagePath);

            // Подставляем суффикс _thumb перед расширением
            var thumbStoragePath = storagePath.Replace(extension, $"_thumb{extension}");

            // Превращаем внутренний путь бэкенда в готовый внешний URL (https://idmbb.ru:8070/...)
            return urlResolver.ResolveUrl(thumbStoragePath);
        }
    }
}
