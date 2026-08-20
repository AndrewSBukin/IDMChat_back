using IDMChat.DTO;
using IDMChat.Hubs;
using IDMChat.Models;
using IDMChat.Utils;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using static AuthController;

namespace IDMChat.Services
{
    public interface IClubSyncService
    {
        Task SyncUserClubsAsync(Guid userId, List<IdmClubDto> idmClubs, CancellationToken cancellationToken);
    }

    public class ClubSyncService : IClubSyncService
    {
        private readonly ChatDbContext _db;
        private readonly IAuthContextService _authContextService;
        private readonly IHubContext<ChatHub> _hubContext;
        private readonly UserCache _userCache;

        public ClubSyncService(
            ChatDbContext db,
            IAuthContextService authContextService,
            IHubContext<ChatHub> hubContext,
            UserCache userCache)
        {
            _db = db;
            _authContextService = authContextService;
            _hubContext = hubContext;
            _userCache = userCache;
        }

        public async Task SyncUserClubsAsync(Guid userId, List<IdmClubDto> idmClubs, CancellationToken cancellationToken)
        {
            if (idmClubs == null)
            {
                return;
            }

            // 1. Получаем текущие ID клубов пользователя из нашей локальной БД
            var localClubIds = await _db.UserClubs
                .Where(uc => uc.UserId == userId)
                .Select(uc => uc.ClubId)
                .ToListAsync(cancellationToken);

            var idmClubIds = idmClubs.Select(c => c.Id).ToList();

            // 2. Сравниваем списки. Если они идентичны — ничего не делаем (экономим ресурсы)
            if (localClubIds.Count == idmClubIds.Count && !localClubIds.Except(idmClubIds).Any())
            {
                return;
            }

            // 3. Начинаем транзакцию для обеспечения целостности данных
            var executionStrategy = _db.Database.CreateExecutionStrategy();
            await executionStrategy.ExecuteAsync(async () =>
            { 
                using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
                try
                {
                    // Сначала гарантируем, что сами справочники клубов актуальны в таблице Club
                    foreach (var idmClub in idmClubs)
                    {
                        var exists = await _db.Clubs.AnyAsync(c => c.Id == idmClub.Id, cancellationToken);
                        if (!exists)
                        {
                            _db.Clubs.Add(new Club
                            {
                                Id = idmClub.Id,
                                Name = idmClub.Name,
                                Code = idmClub.Code ?? "",
                                Idm = idmClub.Idm,
                                CityName = idmClub.CityName,
                                CityGmt = idmClub.CityGmt
                            });
                        }
                    }
                    await _db.SaveChangesAsync(cancellationToken);

                    // Пачечное удаление старых связей через EF Core 8 ExecuteDeleteAsync
                    await _db.UserClubs
                        .Where(uc => uc.UserId == userId)
                        .ExecuteDeleteAsync(cancellationToken);

                    // Вставка новых связей
                    var newConnections = idmClubs.Select(c => new UserClub { UserId = userId, ClubId = c.Id });
                    _db.UserClubs.AddRange(newConnections);
                    await _db.SaveChangesAsync(cancellationToken);

                    await transaction.CommitAsync(cancellationToken);
                }
                catch
                {
                    await transaction.RollbackAsync(cancellationToken);
                    throw;
                }
            });

            // 4. Инвалидируем кэш прав в памяти бэкенда
            _authContextService.InvalidateCache(userId);

            // 5. Оповещаем пользователя в реалтайме через SignalR, если он онлайн
            if (_userCache.IsOnline(userId))
            {
                var clubsPayload = idmClubs.Select(ClubMapper.ToFrontendDto).ToList();

                // Отправляем изолированное событие, чтобы фронтенд перестроил меню/права без разрыва связи
                await _hubContext.Clients.User(userId.ToString()).SendAsync("user_clubs_updated", clubsPayload, cancellationToken);
            }
        }
    }

    public class OnlineUsersClubSyncWorker : BackgroundService
    {
        private readonly UserCache _userCache;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<OnlineUsersClubSyncWorker> _logger;

        private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(15);
        private const int MaxParallelRequests = 5;

        public OnlineUsersClubSyncWorker(
            UserCache userCache,
            IServiceProvider serviceProvider,
            ILogger<OnlineUsersClubSyncWorker> logger)
        {
            _userCache = userCache;
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Фоновый воркер синхронизации клубов ИДМ запущен.");

            using var timer = new PeriodicTimer(SyncInterval);

            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var onlineUserIds = _userCache.GetOnlineMembers();

                    if (onlineUserIds == null || !onlineUserIds.Any())
                    {
                        continue;
                    }

                    // Создаем временный Scope, чтобы вычитать связку Id и внешнего ключа idm из БД
                    using var initScope = _serviceProvider.CreateScope();
                    var db = initScope.ServiceProvider.GetRequiredService<ChatDbContext>();

                    // Вытягиваем только тех, у кого заполнен идентификатор ИДМ
                    var userMapping = await db.Users
                        .Where(u => onlineUserIds.Contains(u.Id) && u.IdmUserId != null)
                        .Select(u => new { u.Id, u.IdmUserId.Value })
                        .ToListAsync(stoppingToken);

                    _logger.LogInformation("Запущена пачечная проверка клубов для {Count} онлайн-пользователей.", onlineUserIds.Count);

                    var parallelOptions = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = MaxParallelRequests,
                        CancellationToken = stoppingToken
                    };

                    await Parallel.ForEachAsync(userMapping, parallelOptions, async (user, token) =>
                    {
                        using var scope = _serviceProvider.CreateScope();

                        var idmClient = scope.ServiceProvider.GetRequiredService<IIdmApiClient>();
                        var clubSyncService = scope.ServiceProvider.GetRequiredService<IClubSyncService>();

                        // Инициализируем и запускаем таймер перед запросом к ИДМ
                        var stopwatch = Stopwatch.StartNew();

                        try
                        {
                            var idmClubs = await idmClient.GetUserClubsAsync(user.Value, token);

                            // Останавливаем таймер сразу после получения ответа
                            stopwatch.Stop();

                            // Логируем время ответа для сбора статистики
                            _logger.LogInformation("ИДМ ответила за {ElapsedMs} мс для пользователя {UserId}.",
                                stopwatch.ElapsedMilliseconds, user.Id);

                            if (idmClubs != null)
                            {
                                await clubSyncService.SyncUserClubsAsync(user.Id, idmClubs, token);
                            }
                        }
                        catch (Exception ex)
                        {
                            stopwatch.Stop();
                            _logger.LogWarning(ex, "Ошибка или таймаут запроса к ИДМ для пользователя {UserId} после {ElapsedMs} мс.",
                                user.Id, stopwatch.ElapsedMilliseconds);
                        }
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Критическая ошибка в цикле фоновой синхронизации клубов.");
                }
            }
        }
    }
}
