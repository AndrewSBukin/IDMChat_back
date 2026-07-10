using Asp.Versioning;
using IDMChat.DTO;
using IDMChat.Middleware;
using IDMChat.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IDMChat.Controllers
{
    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    [Authorize]
    [ApiVersion("1.0")]
    public class NotificationsController : ControllerBase
    {
        private readonly ChatDbContext _db;

        public NotificationsController(ChatDbContext dbContext)
        {
            _db = dbContext;
        }

        // 1. ПОСТ РЕГИСТРАЦИИ ТОКЕНА
        [HttpPost("register-token")]
        public async Task<IActionResult> RegisterToken([FromBody] RegisterTokenRequest req, CancellationToken ct)
        {
            var userId = HttpContext.GetCurrentUserId();

            if (string.IsNullOrWhiteSpace(req.token) || string.IsNullOrWhiteSpace(req.deviceId))
                return BadRequest(new { error = new { code = "INVALID_DATA", message = "token и deviceId обязательны" } });

            // Ищем, нет ли уже этого устройства у пользователя
            var existingToken = await _db.DeviceTokens
                .FirstOrDefaultAsync(t => t.UserId == userId && t.DeviceId == req.deviceId, ct);

            if (existingToken != null)
            {
                // Обновляем токен, если он изменился
                existingToken.Token = req.token;
                existingToken.Platform = req.platform.ToLower();
                existingToken.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                // Если зашли под новой учеткой на том же девайсе — удаляем этот девайс у старых юзеров
                var oldDeviceOwners = _db.DeviceTokens.Where(t => t.DeviceId == req.deviceId);
                _db.DeviceTokens.RemoveRange(oldDeviceOwners);

                // Добавляем новую привязку
                _db.DeviceTokens.Add(new DeviceToken
                {
                    UserId = userId,
                    DeviceId = req.deviceId,
                    Token = req.token,
                    Platform = req.platform.ToLower(),
                    UpdatedAt = DateTime.UtcNow
                });
            }

            await _db.SaveChangesAsync(ct);
            return Ok(new { success = true });
        }

        // 2. УДАЛЕНИЕ ТОКЕНА ПРИ ЛОГАУТЕ
        [HttpDelete("register-token")]
        public async Task<IActionResult> DeleteToken([FromBody] DeleteTokenRequest req, CancellationToken ct)
        {
            var userId = HttpContext.GetCurrentUserId();

            var tokenRecord = await _db.DeviceTokens
                .FirstOrDefaultAsync(t => t.UserId == userId && t.DeviceId == req.deviceId, ct);

            if (tokenRecord != null)
            {
                _db.DeviceTokens.Remove(tokenRecord);
                await _db.SaveChangesAsync(ct);
            }

            return Ok(new { success = true });
        }

    }
}
