using Asp.Versioning;
using IDMChat.Middleware;
using IDMChat.Models;
using IDMChat.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace IDMChat.Controllers
{

    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    [Authorize]
    [ApiVersion("1.0")]
    public class SettingsController : ControllerBase
    {
        private readonly ChatDbContext _db;
        private readonly ILogger<SettingsController> _logger;
        private readonly ChatStateCache _cache;

        public SettingsController(ChatDbContext dbContext, ChatStateCache cache, ILogger<SettingsController> logger)
        {
            _db = dbContext;
            _logger = logger;
            _cache = cache;
        }

        /// <summary>
        /// Получить настройки текущего пользователя
        /// </summary>
        [HttpGet]
        public async Task<ActionResult<UserSettingsResponse>> GetSettings(CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();

            var user = await _db.Users
                .FirstOrDefaultAsync(u => u.Id == userId, ct);

            if (user == null)
                return NotFound(new { error = new { code = "USER_NOT_FOUND", message = "Пользователь не найден" } });

            return Ok(new UserSettingsResponse
            {
                notifications_enabled = user.NotificationsEnabled ?? true,
                sound_enabled = user.SoundEnabled ?? true,
                language = user.Language ?? "ru",
                theme = user.Theme ?? "system"
            });
        }

        /// <summary>
        /// Обновить настройки текущего пользователя (все поля опциональны)
        /// </summary>
        [HttpPatch]
        public async Task<ActionResult<UserSettingsResponse>> UpdateSettings(
            [FromBody] UpdateSettingsRequest request,
            CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();

            var user = await _db.Users
                .FirstOrDefaultAsync(u => u.Id == userId, ct);

            if (user == null)
                return NotFound(new { error = new { code = "USER_NOT_FOUND", message = "Пользователь не найден" } });

            // Обновляем только переданные поля
            if (request.notifications_enabled.HasValue)
                user.NotificationsEnabled = request.notifications_enabled.Value;

            if (request.sound_enabled.HasValue)
                user.SoundEnabled = request.sound_enabled.Value;

            if (!string.IsNullOrWhiteSpace(request.language))
                user.Language = request.language;

            if (!string.IsNullOrWhiteSpace(request.theme))
                user.Theme = request.theme;

            await _db.SaveChangesAsync(ct);

            // Инвалидируем кэш пользователя (если используется)
            _cache.Invalidate(userId);

            return Ok(new UserSettingsResponse
            {
                notifications_enabled = user.NotificationsEnabled ?? true,
                sound_enabled = user.SoundEnabled ?? true,
                language = user.Language ?? "ru",
                theme = user.Theme ?? "system"
            });
        }
    }

    // Request DTO
    public class UpdateSettingsRequest
    {
        public bool? notifications_enabled { get; set; }
        public bool? sound_enabled { get; set; }

        [MaxLength(10)]
        public string? language { get; set; }

        [RegularExpression("^(system|light|dark)$", ErrorMessage = "Theme must be 'system', 'light', or 'dark'")]
        public string? theme { get; set; }
    }

    // Response DTO
    public class UserSettingsResponse
    {
        public bool notifications_enabled { get; set; }
        public bool sound_enabled { get; set; }
        public string language { get; set; } = "ru";
        public string theme { get; set; } = "system";
    }
}
