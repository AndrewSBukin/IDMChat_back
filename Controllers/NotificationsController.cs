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

            var targetPlatform = req.platform?.ToLower() ?? "android";

            // Если этот конкретный FCM-токен уже зарегистрирован в базе за ДРУГИМ пользователем 
            // или на ДРУГОМ deviceId, мы обязаны очистить эти старые неактуальные привязки.
            var duplicateTokens = await _db.DeviceTokens
                .Where(t => t.Token == req.token && (t.UserId != userId || t.DeviceId != req.deviceId))
                .ToListAsync(ct);

            if (duplicateTokens.Any())
            {
                _db.DeviceTokens.RemoveRange(duplicateTokens);
                // Не делаем SaveChangesAsync сразу, EF Core объединит это в одну транзакцию ниже
            }

            // Ищем запись по паре Юзер + Девайс
            var existingTokenByDevice = await _db.DeviceTokens
                .FirstOrDefaultAsync(t => t.UserId == userId && t.DeviceId == req.deviceId, ct);

            if (existingTokenByDevice != null)
            {
                // Если девайс найден — просто обновляем токен (стандартный сценарий)
                existingTokenByDevice.Token = req.token;
                existingTokenByDevice.Platform = targetPlatform;
                existingTokenByDevice.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                // Если пара Юзер+Девайс не найдена, проверяем: возможно у ЭТОГО же юзера 
                // этот же ТОКЕН уже привязан к СТАРЫМУ deviceId (тот самый случай Xcode апдейта)
                var existingTokenByUserAndToken = await _db.DeviceTokens
                            .FirstOrDefaultAsync(t => t.UserId == userId && t.Token == req.token, ct);

                if (existingTokenByUserAndToken != null)
                {
                    // Не плодим строку! Переиспользуем её, обновив изменившийся DeviceId
                    existingTokenByUserAndToken.DeviceId = req.deviceId;
                    existingTokenByUserAndToken.Platform = targetPlatform;
                    existingTokenByUserAndToken.UpdatedAt = DateTime.UtcNow;
                }
                else
                {
                    // Абсолютно новое устройство под новой учетной записью.
                    // На всякий случай чистим старых владельцев этого конкретного DeviceId (ваша исходная логика)
                    var oldDeviceOwners = _db.DeviceTokens.Where(t => t.DeviceId == req.deviceId);
                    _db.DeviceTokens.RemoveRange(oldDeviceOwners);

                    // Создаем чистую привязку
                    _db.DeviceTokens.Add(new DeviceToken
                    {
                        UserId = userId,
                        DeviceId = req.deviceId,
                        Token = req.token,
                        Platform = targetPlatform,
                        UpdatedAt = DateTime.UtcNow
                    });
                }
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
