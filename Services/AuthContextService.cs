using IDMChat.DTO;
using IDMChat.Models;
using IDMChat.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace IDMChat.Services
{
    public interface IAuthContextService
    {
        /// <summary>
        /// Возвращает эффективное меню, права и лимиты пользователя (сначала ищет в локальном кэше)
        /// </summary>
        Task<AuthContextResponse> GetContextAsync(Guid userId, CancellationToken ct = default);

        /// <summary>
        /// Принудительно удаляет данные пользователя из кэша (вызывать при изменении прав в админке)
        /// </summary>
        void InvalidateCache(Guid userId);
    }

    public class AuthContextService : IAuthContextService
    {
        private readonly ChatDbContext _db;
        private readonly IMemoryCache _cache;
        private readonly UserCache _userCache; // 🔥 Внедряем ваш существующий кэш пользователей
        private const string CacheKeyPrefix = "auth_ctx_";

        public AuthContextService(ChatDbContext db, IMemoryCache cache, UserCache userCache)
        {
            _db = db;
            _cache = cache;
            _userCache = userCache;
        }

        public async Task<AuthContextResponse> GetContextAsync(Guid userId, CancellationToken ct = default)
        {
            string cacheKey = $"{CacheKeyPrefix}{userId}";

            return await _cache.GetOrCreateAsync(cacheKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24);
                entry.SlidingExpiration = TimeSpan.FromMinutes(30);

                return await BuildContextFromDbAsync(userId, ct);
            }) ?? throw new InvalidOperationException("Не удалось собрать контекст авторизации");
        }

        public void InvalidateCache(Guid userId)
        {
            string cacheKey = $"{CacheKeyPrefix}{userId}";
            _cache.Remove(cacheKey);
        }

        private async Task<AuthContextResponse> BuildContextFromDbAsync(Guid userId, CancellationToken ct)
        {
            // 1. Получаем профиль роли и семантические настройки приземления
            var userProfileData = await _db.UserProfiles
                .Where(up => up.UserId == userId)
                .Select(up => new
                {
                    up.RoleId,
                    RoleCode = up.Role != null ? up.Role.Code : "user",
                    up.DefaultSectionKey,
                    up.ClubLandingSectionKey
                })
                .FirstOrDefaultAsync(ct);

            // 🔥 УДАЛЕН ЗАПРОС К _db.Users. Вместо него берем имя напрямую из вашего кэша в памяти
            string fullName = _userCache.GetDisplayName(userId) ?? "Сотрудник";

            var roleId = userProfileData?.RoleId;

            // 2. ПАКЕТНЫЙ ЗАПРОС К БД: Выкачиваем все дефолты и оверрайды
            var rolePermissions = roleId != null
                ? await _db.RolePermissions.Where(rp => rp.RoleId == roleId.Value).Select(rp => rp.PermissionKey).ToListAsync(ct)
                : new List<string>();

            var userPermissionOverrides = await _db.UserPermissionOverrides
                .Where(up => up.UserId == userId)
                .Select(up => new { up.PermissionKey, up.Effect })
                .ToListAsync(ct);

            var roleSections = roleId != null
                ? await _db.RoleSections.Where(rs => rs.RoleId == roleId.Value).Select(rs => rs.SectionKey).ToListAsync(ct)
                : new List<string>();

            var userSectionOverrides = await _db.UserSectionOverrides
                .Where(us => us.UserId == userId)
                .Select(us => new { us.SectionKey, us.Effect })
                .ToListAsync(ct);

            var allActiveSections = await _db.Sections.Where(s => s.IsActive).ToListAsync(ct);
            var userLimits = await _db.UserLimits.Where(ul => ul.UserId == userId).ToDictionaryAsync(ul => ul.LimitKey, ul => ul.IntValue, ct);

            // Загрузка тонких DTO доступных пользователю клубов
            var userClubs = await _db.UserClubs
                .Where(uc => uc.UserId == userId)
                .Select(uc => new ThinClubDto
                {
                    id = uc.ClubId,
                    bbid = _db.Clubs.Where(c => c.Id == uc.ClubId).Select(c => c.Code).FirstOrDefault() ?? "",
                    name = _db.Clubs.Where(c => c.Id == uc.ClubId).Select(c => c.Name).FirstOrDefault() ?? "",
                    city = _db.Clubs.Where(c => c.Id == uc.ClubId).Select(c => new CityDto
                    {
                        name = c.CityName,
                        gmt = c.CityGmt
                    }).FirstOrDefault()!
                })
                .ToListAsync(ct);

            // =================================================================================
            // ВЫЧИСЛЕНИЕ ЭФФЕКТИВНЫХ ПРАВ И СБОРКА МЕНЮ
            // =================================================================================
            var effectivePermissions = new HashSet<string>(rolePermissions);
            foreach (var over in userPermissionOverrides)
            {
                if (over.Effect == AccessEffect.Grant) effectivePermissions.Add(over.PermissionKey);
                else effectivePermissions.Remove(over.PermissionKey);
            }

            var effectiveSectionKeys = new HashSet<string>(roleSections);
            foreach (var over in userSectionOverrides)
            {
                if (over.Effect == AccessEffect.Grant) effectiveSectionKeys.Add(over.SectionKey);
                else effectiveSectionKeys.Remove(over.SectionKey);
            }

            var menuItems = new List<MenuItemDto>();
            var rootSections = allActiveSections
                .Where(s => s.ParentKey == null && effectiveSectionKeys.Contains(s.Key))
                .OrderBy(s => s.Order);

            foreach (var root in rootSections)
            {
                var menuItem = new MenuItemDto
                {
                    key = root.Key,
                    scope = root.Scope,
                    title = root.Title,
                    icon = root.Icon,
                    order = root.Order,
                    children = allActiveSections
                        .Where(s => s.ParentKey == root.Key && effectiveSectionKeys.Contains(s.Key))
                        .OrderBy(s => s.Order)
                        .Select(child => new MenuItemChildDto
                        {
                            key = child.Key,
                            title = child.Title,
                            icon = child.Icon,
                            order = child.Order
                        })
                        .ToList()
                };

                menuItems.Add(menuItem);
            }

            SectionPointerDto? defaultSectionPointer = null;
            if (!string.IsNullOrEmpty(userProfileData?.DefaultSectionKey))
            {
                var defSection = allActiveSections.FirstOrDefault(s => s.Key == userProfileData.DefaultSectionKey);
                if (defSection != null)
                {
                    defaultSectionPointer = new SectionPointerDto
                    {
                        key = defSection.Key,
                        scope = defSection.Scope
                    };
                }
            }

            return new AuthContextResponse
            {
                user = new AuthUserDto
                {
                    id = userId.ToString(),
                    fullName = fullName, // 🔥 Используем имя из кэша пользователей
                    role = userProfileData?.RoleCode ?? "user",
                    defaultSection = defaultSectionPointer,
                    clubLandingKey = userProfileData?.ClubLandingSectionKey
                },
                permissions = effectivePermissions.OrderBy(p => p).ToList(),
                limits = userLimits,
                clubs = userClubs,
                menu = menuItems
            };
        }
    }

}
