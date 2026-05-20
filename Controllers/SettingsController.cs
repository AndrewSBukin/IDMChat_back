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
                NotificationsEnabled = user.NotificationsEnabled ?? true,
                SoundEnabled = user.SoundEnabled ?? true,
                Language = user.Language ?? "ru",
                Theme = user.Theme ?? "system"
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
            if (request.NotificationsEnabled.HasValue)
                user.NotificationsEnabled = request.NotificationsEnabled.Value;

            if (request.SoundEnabled.HasValue)
                user.SoundEnabled = request.SoundEnabled.Value;

            if (!string.IsNullOrWhiteSpace(request.Language))
                user.Language = request.Language;

            if (!string.IsNullOrWhiteSpace(request.Theme))
                user.Theme = request.Theme;

            await _db.SaveChangesAsync(ct);

            // Инвалидируем кэш пользователя (если используется)
            _cache.Invalidate(userId);

            return Ok(new UserSettingsResponse
            {
                NotificationsEnabled = user.NotificationsEnabled ?? true,
                SoundEnabled = user.SoundEnabled ?? true,
                Language = user.Language ?? "ru",
                Theme = user.Theme ?? "system"
            });
        }
    }

    // Request DTO
    public class UpdateSettingsRequest
    {
        public bool? NotificationsEnabled { get; set; }
        public bool? SoundEnabled { get; set; }

        [MaxLength(10)]
        public string? Language { get; set; }

        [RegularExpression("^(system|light|dark)$", ErrorMessage = "Theme must be 'system', 'light', or 'dark'")]
        public string? Theme { get; set; }
    }

    // Response DTO
    public class UserSettingsResponse
    {
        public bool NotificationsEnabled { get; set; }
        public bool SoundEnabled { get; set; }
        public string Language { get; set; } = "ru";
        public string Theme { get; set; } = "system";
    }
}
