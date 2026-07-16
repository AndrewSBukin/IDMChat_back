using IDMChat.Models;
using IDMChat.Utils;
using Microsoft.EntityFrameworkCore;

namespace IDMChat.Services
{
    public class CacheWarmupService : IHostedService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly UserCache _userCache;
        private readonly ILogger<CacheWarmupService> _logger;

        public CacheWarmupService(IServiceProvider serviceProvider, UserCache userCache, ILogger<CacheWarmupService> logger)
        {
            _serviceProvider = serviceProvider;
            _userCache = userCache;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Запуск прогрева кэша пользователей...");

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

                var users = await db.Users
                    .Select(u => new UserCache.CachedUser(u.Id, u.DisplayName, u.AvatarUrl, u.CustomStatus, u.LastSeenAt, u.IdmUserId, u.IsActive))
                    .ToListAsync(cancellationToken);

                _userCache.InitializeAllUsers(users);

                _logger.LogInformation("Кэш успешно прогрет. Загружено пользователей: {Count}", users.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Критическая ошибка при прогреве кэша пользователей.");
                throw; // Сбой при старте защитит приложение от работы со сломанным кэшем
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}