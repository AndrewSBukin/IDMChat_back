using IDMChat.Models;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Claims;

namespace IDMChat.Middleware
{
    public class ActiveUserMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IMemoryCache _cache;

        public ActiveUserMiddleware(
            RequestDelegate next, 
            IServiceScopeFactory scopeFactory,
            IMemoryCache cache)
        {
            _next = next;
            _scopeFactory = scopeFactory;
            _cache = cache;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (context.User.Identity?.IsAuthenticated == true)
            {
                var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                if (Guid.TryParse(userIdClaim, out var userId))
                {
                    if (!_cache.TryGetValue($"user_{userId}", out User? user))
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

                        user = await db.Users.FindAsync(userId);
                        if (user != null)
                        {
                            // 3. Кэшируем на 5 минут (скользящее)
                            var cacheOptions = new MemoryCacheEntryOptions
                            {
                                SlidingExpiration = TimeSpan.FromMinutes(5)
                            };
                            _cache.Set($"user_{userId}", user, cacheOptions);
                        }
                    }
                        
                    if (user == null || !user.IsActive)
                    {
                        context.Response.StatusCode = 401;
                        await context.Response.WriteAsJsonAsync(new
                        {
                            error = new { code = "USER_INACTIVE", message = "Пользователь не активен" }
                        });
                        return;
                    }

                    // Прокинуть в контекст для дальнейшего использования
                    context.Items["CurrentUser"] = user;
                }
            }

            await _next(context);
        }
    }

    public static class HttpContextExtensions
    {
        public static User GetCurrentUser(this HttpContext context)
        {
            var user = context.Items["CurrentUser"] as User;
            if (user == null)
                throw new UnauthorizedAccessException(
                    "{\"error\": {\"code\": \"USER_NOT_FOUND\", \"message\": \"Пользователь не найден\"}}");

            return user;
        }

        /// <summary>
        /// Быстрый метод без обращения к базе выдает Id текущего пользователя
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        /// <exception cref="UnauthorizedAccessException"></exception>
        public static Guid GetCurrentUserId(this HttpContext context)
        {
            var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(userIdClaim, out var userId))
                throw new UnauthorizedAccessException(
                    "{\"error\": {\"code\": \"INVALID_USER_ID\", \"message\": \"Неверный идентификатор пользователя\"}}");

            return userId;
        }
    }
}
