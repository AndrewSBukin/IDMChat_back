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

            // 1. Пытаемся получить контекст текущего HTTP-запроса пользователя
            var context = _httpContextAccessor.HttpContext;
            string baseUrl;

            if (context != null && !string.IsNullOrEmpty(context.Request.Host.Value))
            {
                // Собираем домен динамически на основе текущего подключения сотрудника
                baseUrl = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";
            }
            else
            {
                // Если контекста нет (например, фоновый воркер), берем адрес из конфига
                baseUrl = _fallbackBaseUrl.TrimEnd('/');
            }

            // Унифицируем разделители пути (на случай, если в базе где-то проскочил бэкслеш)
            var cleanRelativePath = relativePath.Replace('\\', '/').TrimStart('/');

            // Наш единый эндпоинт раздачи в FilesController — это api/files/{**filePath}
            return $"{baseUrl}/api/files/{cleanRelativePath}";
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
