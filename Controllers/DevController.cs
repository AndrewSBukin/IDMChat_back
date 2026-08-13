using Asp.Versioning;
using IDMChat.Middleware;
using IDMChat.Models;
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

        public DevController(ChatDbContext dbContext)
        {
            _db = dbContext;
        }

        private bool IsSuperAdmin() => User.Identity?.Name == "admin";

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
    }
}
