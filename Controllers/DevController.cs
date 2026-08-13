using Asp.Versioning;
using IDMChat.DTO;
using IDMChat.Middleware;
using IDMChat.Models;
using IDMChat.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IDMChat.Controllers
{
    [Route("api/v{version:apiVersion}/[controller]")]
    [ApiController]
    [ApiVersion("1.0")]
    //[AllowAnonymous]
    public class DevController : ControllerBase
    {
        private readonly ChatDbContext _db;
        private readonly IAuthContextService _authContextService;

        public DevController(ChatDbContext dbContext, IAuthContextService authContextService)
        {
            _db = dbContext;
            _authContextService = authContextService;
        }

        [HttpGet("health")]
        public IActionResult Health()
        {
            return Ok(new { status = "OK", timestamp = DateTime.UtcNow });
        }

        [HttpPost("create-user")]
        public async Task<IActionResult> DebugCreateUser([FromBody] DebugCreateUserRequest request)
        {
            var exists = await _db.Users
                .AnyAsync(u => u.Username == request.Username);

            if (exists)
            {
                return BadRequest(new
                {
                    error = new
                    {
                        code = "USER_EXISTS",
                        message = "Пользователь уже существует"
                    }
                });
            }

            var user = new User
            {
                Id = Guid.NewGuid(),
                Username = request.Username,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
                DisplayName = request.DisplayName ?? request.Username,
                AvatarUrl = request.AvatarUrl,
                ConnectionId = string.Empty,
                idm = request.Idm,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            _db.Users.Add(user);
            await _db.SaveChangesAsync();

            return Ok(new
            {
                id = user.Id,
                username = user.Username,
                displayName = user.DisplayName,
                message = "Пользователь создан"
            });
        }
        public class DebugCreateUserRequest
        {
            public string Username { get; set; } = string.Empty;
            public string Password { get; set; } = string.Empty;
            public string? DisplayName { get; set; }
            public string? Idm { get; set; }
            public string? AvatarUrl { get; set; }
        }

        /// <summary>
        /// Запрос всей структуры матрицы для отрисовки интерфейса
        /// </summary>
        [HttpGet("matrix")]
        public async Task<IActionResult> GetMatrix()
        {
            var userId = HttpContext.GetCurrentUserId();
            var currentUser = await _db.Users.FindAsync(new object[] { userId });
            if (currentUser.IdmRole != "administrator") return Forbid();

            var roles = await _db.Set<Role>().ToListAsync();
            var permissions = await _db.Set<Permission>().ToListAsync();
            var sections = await _db.Set<Section>().ToListAsync();

            var activePermissions = await _db.Set<RolePermission>().ToListAsync();
            var activeSections = await _db.Set<RoleSection>().ToListAsync();

            return Ok(new
            {
                roles = roles.Select(r => new { r.Id, r.Name }),
                permissions = permissions.Select(p => new { p.Key, p.Description }),
                sections = sections.Select(s => new { s.Key, s.Title }),
                // Возвращаем хэш-сеты для мгновенной проверки чекбоксов на фронтенде
                activePermissions = activePermissions.Select(rp => $"{rp.RoleId}_{rp.PermissionKey}").ToHashSet(),
                activeSections = activeSections.Select(rs => $"{rs.RoleId}_{rs.SectionKey}").ToHashSet()
            });
        }

        /// <summary>
        /// Единая RPC-команда для изменения состояния чекбокса
        /// </summary>
        [HttpPost("matrix/toggle")]
        public async Task<IActionResult> ToggleAccess([FromBody] ToggleAccessCommand cmd)
        {
            var userId = HttpContext.GetCurrentUserId();
            var currentUser = await _db.Users.FindAsync(new object[] { userId });
            if (currentUser.IdmRole != "administrator") return Forbid();

            // 1. Обработка переключения атомарного ПРАВА
            if (cmd.Type == "permission")
            {
                var entity = await _db.Set<RolePermission>()
                    .FirstOrDefaultAsync(rp => rp.RoleId == cmd.RoleId && rp.PermissionKey == cmd.Key);

                if (cmd.IsChecked && entity == null)
                {
                    _db.Set<RolePermission>().Add(new RolePermission { RoleId = cmd.RoleId, PermissionKey = cmd.Key });
                }
                else if (!cmd.IsChecked && entity != null)
                {
                    _db.Set<RolePermission>().Remove(entity);
                }
            }
            // 2. Обработка переключения РАЗДЕЛА МЕНЮ
            else if (cmd.Type == "section")
            {
                var entity = await _db.Set<RoleSection>()
                    .FirstOrDefaultAsync(rs => rs.RoleId == cmd.RoleId && rs.SectionKey == cmd.Key);

                if (cmd.IsChecked && entity == null)
                {
                    _db.Set<RoleSection>().Add(new RoleSection { RoleId = cmd.RoleId, SectionKey = cmd.Key });
                }
                else if (!cmd.IsChecked && entity != null)
                {
                    _db.Set<RoleSection>().Remove(entity);
                }
            }
            else
            {
                return BadRequest("Неверный тип целевого объекта доступов");
            }

            await _db.SaveChangesAsync();
            return Ok(new { success = true });
        }

        public record ToggleAccessCommand(Guid RoleId, string Key, string Type, bool IsChecked);


        // Модель для входящего запроса на переключение оверрайда
        public record ToggleOverrideCommand(Guid UserId, string Key, string Type, int EffectMode);
        // EffectMode: 0 = По роли (удалить оверрайд), 1 = Grant (Разрешить), 2 = Deny (Запретить)

        [HttpGet("matrix/users")]
        public async Task<IActionResult> GetUserMatrix([FromQuery] string search = "")
        {
            if (!await IsSuperAdmin()) return Forbid();

            // 1. Фильтруем пользователей (берем только топ-30 во избежание перегрузки DOM)
            var query = _db.Set<User>().Where(u => u.IsActive);
            if (!string.IsNullOrEmpty(search))
            {
                query = query.Where(u => u.DisplayName.Contains(search) || u.Username.Contains(search));
            }
            var users = await query.OrderBy(u => u.DisplayName).Take(30).ToListAsync();
            var userIds = users.Select(u => u.Id).ToList();

            // 2. Вычитываем роли этих пользователей (для расчета базового доступа)
            var userProfiles = await _db.Set<UserProfile>()
                .Where(up => userIds.Contains(up.UserId))
                .ToListAsync();

            // 3. Вычитываем оси прав и разделов
            var permissions = await _db.Set<Permission>().OrderBy(p => p.Key).ToListAsync();
            var sections = await _db.Set<Section>().OrderBy(s => s.Key).ToListAsync();

            // 4. Вычитываем матрицу базовых прав ролей (чтобы знать, какой доступ идет "по роли")
            var rolePermissions = await _db.Set<RolePermission>().ToListAsync();
            var roleSections = await _db.Set<RoleSection>().ToListAsync();

            // 5. Вычитываем текущие персональные оверрайды выбранных пользователей
            var userPermissionOverrides = await _db.Set<UserPermissionOverride>()
                .Where(o => userIds.Contains(o.UserId)).ToListAsync();

            var userSectionOverrides = await _db.Set<UserSectionOverride>()
                .Where(o => userIds.Contains(o.UserId)).ToListAsync();

            return Ok(new
            {
                users = users.Select(u => new {
                    u.Id,
                    u.DisplayName,
                    u.Username,
                    roleId = userProfiles.FirstOrDefault(p => p.UserId == u.Id)?.RoleId
                }),
                permissions = permissions.Select(p => new { p.Key, p.Description }),
                sections = sections.Select(s => new { s.Key, s.Title }),

                // Базовые права ролей для вычисления наследования: "RoleId_Key"
                rolePermissions = rolePermissions.Select(rp => $"{rp.RoleId}_{rp.PermissionKey}").ToHashSet(),
                roleSections = roleSections.Select(rs => $"{rs.RoleId}_{rs.SectionKey}").ToHashSet(),

                // Текущие оверрайды в формате: "UserId_Key" -> значение Effect (1 = Grant, 2 = Deny)
                userPermissionOverrides = userPermissionOverrides.ToDictionary(o => $"{o.UserId}_{o.PermissionKey}", o => (int)o.Effect),
                userSectionOverrides = userSectionOverrides.ToDictionary(o => $"{o.UserId}_{o.SectionKey}", o => (int)o.Effect)
            });
        }

        [HttpPost("matrix/users/toggle")]
        public async Task<IActionResult> ToggleUserOverride([FromBody] ToggleOverrideCommand cmd)
        {
            if (!await IsSuperAdmin()) return Forbid();

            // Режим 0: "По роли" — удаляем любые персональные оверрайды, возвращаясь к наследованию
            if (cmd.EffectMode == 0)
            {
                if (cmd.Type == "permission")
                {
                    var entity = await _db.Set<UserPermissionOverride>().FirstOrDefaultAsync(o => o.UserId == cmd.UserId && o.PermissionKey == cmd.Key);
                    if (entity != null) _db.Set<UserPermissionOverride>().Remove(entity);
                }
                else
                {
                    var entity = await _db.Set<UserSectionOverride>().FirstOrDefaultAsync(o => o.UserId == cmd.UserId && o.SectionKey == cmd.Key);
                    if (entity != null) _db.Set<UserSectionOverride>().Remove(entity);
                }
            }
            // Режимы 1 и 2: Явный Grant (1) или Deny (2)
            else
            {
                var targetEffect = cmd.EffectMode == 1 ? AccessEffect.Grant : AccessEffect.Deny;

                if (cmd.Type == "permission")
                {
                    var entity = await _db.Set<UserPermissionOverride>().FirstOrDefaultAsync(o => o.UserId == cmd.UserId && o.PermissionKey == cmd.Key);
                    if (entity != null) entity.Effect = targetEffect;
                    else _db.Set<UserPermissionOverride>().Add(new UserPermissionOverride { UserId = cmd.UserId, PermissionKey = cmd.Key, Effect = targetEffect });
                }
                else
                {
                    var entity = await _db.Set<UserSectionOverride>().FirstOrDefaultAsync(o => o.UserId == cmd.UserId && o.SectionKey == cmd.Key);
                    if (entity != null) entity.Effect = targetEffect;
                    else _db.Set<UserSectionOverride>().Add(new UserSectionOverride { UserId = cmd.UserId, SectionKey = cmd.Key, Effect = targetEffect });
                }
            }

            await _db.SaveChangesAsync();

            // Сразу инвалидируем кэш прав этого пользователя в оперативной памяти бэкенда!
            _authContextService.InvalidateCache(cmd.UserId);

            return Ok(new { success = true });
        }

        // Вспомогательный метод проверки суперадмина на основе вашего кода
        private async Task<bool> IsSuperAdmin()
        {
            var userId = HttpContext.GetCurrentUserId();
            var currentUser = await _db.Users.FindAsync(new object[] { userId });
            return currentUser?.IdmRole == "administrator";
        }

        public record ChangeUserRoleCommand(Guid UserId, Guid? RoleId);

        [HttpPost("matrix/users/change-role")]
        public async Task<IActionResult> ChangeUserRole([FromBody] ChangeUserRoleCommand cmd)
        {
            if (!await IsSuperAdmin()) return Forbid();

            // Находим существующий профиль или создаем новый, если это первый вход
            var profile = await _db.Set<UserProfile>().FirstOrDefaultAsync(p => p.UserId == cmd.UserId);

            if (profile != null)
            {
                profile.RoleId = cmd.RoleId; // Назначаем новую роль (или null)
            }
            else
            {
                _db.Set<UserProfile>().Add(new UserProfile
                {
                    UserId = cmd.UserId,
                    RoleId = cmd.RoleId,
                    DefaultSectionKey = "app.chat" // Дефолтный приземляющий экран
                });
            }

            await _db.SaveChangesAsync();

            // Критично: сбрасываем кэш прав в памяти, чтобы новые права роли вступили в силу мгновенно!
            _authContextService.InvalidateCache(cmd.UserId);

            return Ok(new { success = true });
        }

    }
}
