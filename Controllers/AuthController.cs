using IDMChat.Models;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BCrypt.Net;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly ChatDbContext _dbContext;
    private readonly IConfiguration _config;

    public AuthController(ChatDbContext dbContext, IConfiguration config)
    {
        _dbContext = dbContext;
        _config = config;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            return Unauthorized(new
            {
                error = new
                {
                    code = "INVALID_CREDENTIALS",
                    message = "Неверный логин или пароль"
                }
            });
        }

        var user = await _dbContext.Users
        .FirstOrDefaultAsync(u => u.Username == request.Username && u.IsActive);

        if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            return Unauthorized(new
            {
                error = new
                {
                    code = "INVALID_CREDENTIALS",
                    message = "Неверный логин или пароль"
                }
            });
        }

        // Обновляем данные пользователя
        user.LastLoginAt = DateTime.UtcNow;
        user.LastSeenAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        // Генерируем токены
        var userDto = new UserDto
        {
            Id = user.Id,
            Username = user.Username,
            DisplayName = user.DisplayName ?? user.Username,
            AvatarUrl = user.AvatarUrl
        };

        var accessToken = GenerateAccessToken(userDto);
        var refreshToken = GenerateRefreshToken();
        var expiresIn = Convert.ToInt32(_config["Jwt:ExpiryMinutes"]) * 60;

        // Сохраняем refresh token в БД
        var refreshTokenEntity = new RefreshToken
        {
            Token = refreshToken,
            UserId = user.Id,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedByIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        };
        _dbContext.RefreshTokens.Add(refreshTokenEntity);
        await _dbContext.SaveChangesAsync();

        return Ok(new
        {
            access_token = accessToken,
            refresh_token = refreshToken,
            expires_in = expiresIn,
            user = userDto
        });
    }
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request)
    {
        if (string.IsNullOrEmpty(request?.RefreshToken))
        {
            return Unauthorized(new
            {
                error = new
                {
                    code = "TOKEN_INVALID",
                    message = "Неверный или отсутствующий refresh token"
                }
            });
        }

        // Ищем refresh token в БД
        var refreshToken = await _dbContext.RefreshTokens
            .Include(rt => rt.User)
            .FirstOrDefaultAsync(rt => rt.Token == request.RefreshToken);

        if (refreshToken == null)
        {
            return Unauthorized(new
            {
                error = new
                {
                    code = "TOKEN_INVALID",
                    message = "Неверный refresh token"
                }
            });
        }

        // Проверяем, не отозван ли токен
        if (refreshToken.IsRevoked)
        {
            return Unauthorized(new
            {
                error = new
                {
                    code = "TOKEN_REVOKED",
                    message = "Refresh token отозван"
                }
            });
        }

        // Проверяем, не просрочен ли токен
        if (refreshToken.IsExpired)
        {
            return Unauthorized(new
            {
                error = new
                {
                    code = "TOKEN_EXPIRED",
                    message = "Refresh token истек, требуется повторная авторизация"
                }
            });
        }

        // Получаем пользователя
        var user = refreshToken.User;

        if (user == null || !user.IsActive)
        {
            return Unauthorized(new
            {
                error = new
                {
                    code = "USER_INACTIVE",
                    message = "Пользователь неактивен"
                }
            });
        }

        // Создаем новый access token
        var userDto = new UserDto
        {
            Id = user.Id,
            Username = user.Username,
            DisplayName = user.DisplayName ?? user.Username,
            AvatarUrl = user.AvatarUrl
        };

        var accessToken = GenerateAccessToken(userDto);
        var expiresIn = Convert.ToInt32(_config["Jwt:ExpiryMinutes"]) * 60;

        // Опционально: обновляем дату последнего использования refresh token
        refreshToken.CreatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        return Ok(new
        {
            access_token = accessToken,
            expires_in = expiresIn
        });
    }

    public class RefreshRequest
    {
        public string RefreshToken { get; set; }
    }
    public class UserDto
    {
        public Guid Id { get; set; }
        public string Username { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? AvatarUrl { get; set; }
        public bool IsOnline { get; set; }
        public DateTime LastSeenAt { get; set; }
    }

    private string GenerateAccessToken(UserDto user)
    {
        var claims = new[]
        {
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.Username),
        new Claim("display_name", user.DisplayName ?? user.Username)
    };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config["Jwt:Key"]));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"],
            audience: _config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(Convert.ToDouble(_config["Jwt:ExpiryMinutes"])),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private string GenerateRefreshToken()
    {
        return Convert.ToBase64String(Guid.NewGuid().ToByteArray());
    }


    public class LoginRequest
    {
        public string Username { get; set; }
        public string Password { get; set; }
    }
}


