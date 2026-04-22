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
            status = GetStatus(user),
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

        await _dbContext.SaveChangesAsync();

        return Ok(new
        {
            id = user.Id,
            username = user.Username,
            display_name = string.IsNullOrEmpty(user.DisplayName) ? user.Username : user.DisplayName,
            avatar_url = user.AvatarUrl,
            phone = user.Phone ?? string.Empty,
            email = user.Email ?? string.Empty,
            status = GetStatus(user),
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
        var uploadsFolder = Path.Combine(_env.WebRootPath ?? "wwwroot", "avatars");
        if (!Directory.Exists(uploadsFolder))
            Directory.CreateDirectory(uploadsFolder);

        // Генерируем уникальное имя файла
        var fileName = $"{userId}_{DateTime.UtcNow.Ticks}{extension}";
        var filePath = Path.Combine(uploadsFolder, fileName);

        // Сохраняем файл
        using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        // Формируем URL
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var avatarUrl = $"{baseUrl}/avatars/{fileName}";

        // Удаляем старый аватар, если есть
        if (!string.IsNullOrEmpty(user.AvatarUrl))
        {
            var oldFilePath = Path.Combine(_env.WebRootPath ?? "wwwroot", user.AvatarUrl.Replace(baseUrl, "").TrimStart('/'));
            if (System.IO.File.Exists(oldFilePath))
                System.IO.File.Delete(oldFilePath);
        }

        user.AvatarUrl = avatarUrl;
        await _dbContext.SaveChangesAsync();

        return Ok(new { avatar_url = avatarUrl });
    }

    private Guid GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.Parse(userIdClaim ?? Guid.Empty.ToString());
    }

    private string GetStatus(User user)
    {
        if (!user.IsActive)
            return "offline";

        if (user.IsOnline)
        {
            if (user.LastSeenAt < DateTime.UtcNow.AddMinutes(-5))
                return "away";
            return "online";
        }

        return "offline";
    }
}

public class UpdateProfileRequest
{
    public string? display_name { get; set; }
    public string? phone { get; set; }
    public string? email { get; set; }
    public string? status { get; set; }
}