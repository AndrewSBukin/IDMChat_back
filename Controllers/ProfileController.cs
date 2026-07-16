using Asp.Versioning;
using IDMChat.DTO;
using IDMChat.Middleware;
using IDMChat.Models;
using IDMChat.Services;
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
    private readonly IChatPathUrlResolver _urlResolver;
    private readonly string _storageBasePath;

    public ProfileController(ChatDbContext dbContext, IWebHostEnvironment env, IConfiguration configuration, IChatPathUrlResolver urlResolver)
    {
        _dbContext = dbContext;
        _env = env;
        _config = configuration;
        _storageBasePath = configuration["Storage:BasePath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "uploads");
        _urlResolver = urlResolver;
    }

    [HttpGet("")]
    public async Task<IActionResult> GetProfile()
    {
        var user = HttpContext.GetCurrentUser();

        if (user == null) return NotFound();

        return Ok(new ProfileDto()
        {
            id = user.Id,
            username = user.Username,
            display_name = string.IsNullOrEmpty(user.DisplayName) ? user.Username : user.DisplayName,
            avatar_url = _urlResolver.ResolveUrl(user.AvatarUrl),
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
        var userId = HttpContext.GetCurrentUserId();
        var user = HttpContext.GetCurrentUser();

        if (user == null)
            return NotFound();

        if (request.display_name != null)
        {
            user.DisplayName = request.display_name;
            user.IsDisplayNameCustom = true;
        }

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

        return Ok(new ProfileDto()
        {
            id = user.Id,
            username = user.Username,
            display_name = string.IsNullOrEmpty(user.DisplayName) ? user.Username : user.DisplayName,
            avatar_url = _urlResolver.ResolveUrl(user.AvatarUrl),
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

        var userId = HttpContext.GetCurrentUserId();
        var user = HttpContext.GetCurrentUser();

        if (user == null)
            return NotFound(new { error = new { code = "NO_USER", message = "Пользователь не найден" } });

        // Генерируем уникальное имя файла
        var subFolder = userId.ToString().Substring(0, 2); // "a1"

        // Создаем папку для аватаров
        var userFolder = Path.Combine(_storageBasePath, "avatars", "users", subFolder);
        Directory.CreateDirectory(userFolder);

        var fileName = $"{userId}_{DateTime.UtcNow.Ticks}{extension}";
        var filePath = Path.Combine(userFolder, fileName);

        // Сохраняем файл
        using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        // Удаляем старый аватар, если есть
        if (!string.IsNullOrEmpty(user.AvatarUrl))
        {
            var oldFilePath = Path.Combine(_storageBasePath, user.AvatarUrl.Replace('/', Path.DirectorySeparatorChar));
            _ = Task.Run(() => { 
                if (System.IO.File.Exists(oldFilePath))
                    System.IO.File.Delete(oldFilePath); });
        }

        var relativePath = $"avatars/users/{subFolder}/{fileName}";
        user.AvatarUrl = relativePath;
        _dbContext.Users.Update(user);
        await _dbContext.SaveChangesAsync();

        return Ok(new AvatarDto (){ avatar_url = _urlResolver.ResolveUrl(relativePath) });
    }
}