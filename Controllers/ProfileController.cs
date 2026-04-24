using Asp.Versioning;
using IDMChat.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace IDMChat.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
[Authorize]
[ApiVersion("1.0")]
public class ProfileController : ControllerBase
{
    private readonly ChatDbContext _dbContext;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;

    public ProfileController(ChatDbContext dbContext, IWebHostEnvironment env, IConfiguration config)
    {
        _dbContext = dbContext;
        _env = env;
        _config = config;
    }

    [HttpGet("")]
    public async Task<IActionResult> GetProfile()
    {
        var userId = GetCurrentUserId();
        var user = await _dbContext.Users.FindAsync(userId);

        if (user == null)
            return NotFound();

        return Ok(new
        {
            id = user.Id,
            username = user.Username,
            display_name = string.IsNullOrEmpty(user.DisplayName) ? user.Username : user.DisplayName,
            avatar_url = user.AvatarUrl,
            phone = user.Phone ?? string.Empty,
            email = user.Email ?? string.Empty,
            status = user.Status.ToString().ToLowerInvariant(),
            custom_status = user.CustomStatus,
            is_online = user.IsOnline,
            last_seen_at = user.LastSeenAt
        });
    }

    [HttpPatch("")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request)
    {
        var userId = GetCurrentUserId();
        var user = await _dbContext.Users.FindAsync(userId);

        if (user == null)
            return NotFound();

        if (request.display_name != null)
            user.DisplayName = request.display_name;

        if (request.phone != null)
            user.Phone = request.phone;

        if (request.email != null)
            user.Email = request.email;

        if (request.status.HasValue)
            user.Status = request.status.Value;

        if (request.custom_status != null)
            user.CustomStatus = request.custom_status;

        _dbContext.Users.Update(user);
        await _dbContext.SaveChangesAsync();

        return Ok(new
        {
            id = user.Id,
            username = user.Username,
            display_name = string.IsNullOrEmpty(user.DisplayName) ? user.Username : user.DisplayName,
            avatar_url = user.AvatarUrl,
            phone = user.Phone ?? string.Empty,
            email = user.Email ?? string.Empty,
            status = user.Status.ToString().ToLowerInvariant(),
            custom_status = user.CustomStatus,
            is_online = user.IsOnline,
            last_seen_at = user.LastSeenAt
        });
    }

    [HttpPost("avatar")]
    public async Task<IActionResult> UploadAvatar(IFormFile file)
    {
        // Проверка размера (5MB)
        if (file == null || file.Length == 0)
            return UnprocessableEntity(new { error = new { code = "NO_FILE", message = "Файл не выбран" } });

        if (file.Length > 5 * 1024 * 1024)
            return UnprocessableEntity(new { error = new { code = "FILE_TOO_LARGE", message = "Файл превышает 5MB" } });

        // Проверка формата
        var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();

        if (!allowedExtensions.Contains(extension))
            return UnprocessableEntity(new { error = new { code = "INVALID_FORMAT", message = "Поддерживаются только JPG, PNG, GIF, WEBP" } });

        var userId = GetCurrentUserId();
        var user = await _dbContext.Users.FindAsync(userId);

        if (user == null)
            return NotFound(new { error = new { code = "NO_USER", message = "Пользователь не найден" } });

        // Создаем папку для аватаров
        var uploadsFolder = Path.Combine(_env.WebRootPath ?? "wwwroot", "media", "avatars");
        if (!Directory.Exists(uploadsFolder))
            Directory.CreateDirectory(uploadsFolder);

        // Генерируем уникальное имя файла
        var subFolder = userId.ToString().Substring(0, 2); // "a1"
        var userFolder = Path.Combine(uploadsFolder, subFolder);
        if (!Directory.Exists(userFolder))
            Directory.CreateDirectory(userFolder);
        var fileName = $"{userId}_{DateTime.UtcNow.Ticks}{extension}";
        var filePath = Path.Combine(userFolder, fileName);

        // Сохраняем файл
        using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        // Формируем URL
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var avatarUrl = $"{baseUrl}/media/avatars/{subFolder}/{fileName}";

        // Удаляем старый аватар, если есть
        if (!string.IsNullOrEmpty(user.AvatarUrl))
        {
            var oldFilePath = Path.Combine(_env.WebRootPath ?? "wwwroot", user.AvatarUrl.Replace(baseUrl, "").TrimStart('/'));
            _ = Task.Run(() => { 
                if (System.IO.File.Exists(oldFilePath))
                    System.IO.File.Delete(oldFilePath); });
        }

        user.AvatarUrl = avatarUrl;
        _dbContext.Users.Update(user);
        await _dbContext.SaveChangesAsync();

        return Ok(new { avatar_url = avatarUrl });
    }

    private Guid GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.Parse(userIdClaim ?? Guid.Empty.ToString());
    }
}

public class UpdateProfileRequest
{
    public string? display_name { get; set; }
    public string? phone { get; set; }
    public string? email { get; set; }
    public UserPresenceStatus? status { get; set; }
    public string? custom_status { get; set; }
}