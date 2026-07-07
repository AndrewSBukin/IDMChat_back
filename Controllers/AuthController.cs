using Asp.Versioning;
using IDMChat.Models;
using IDMChat.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class AuthController : ControllerBase
{
    private readonly ChatDbContext _dbContext;
    private readonly IConfiguration _config;
    private readonly IChatPathUrlResolver _urlResolver;

    public AuthController(ChatDbContext dbContext, IConfiguration config, IChatPathUrlResolver urlResolver)
    {
        _dbContext = dbContext;
        _config = config;
        _urlResolver = urlResolver;
    }

    class IdmUser
    {
        public int Id { get; set; }
        public string Username { get; set; }
        public string Fio { get; set; }
        public string Password { get; set; }
        public string Idm { get; set; }
        public bool Blocked { get; set; }
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
        bool useIdmDb = false;

        IdmUser idmUser = new IdmUser();
        if (useIdmDb)
        {
            using SqlConnection con = new SqlConnection("");
            con.Open();
            using SqlCommand cmd = con.CreateCommand();
            cmd.CommandText = "SELECT s.id, fio, username, idm, p.password, case when ISNULL([isBlocked],0) = 1 OR ISNULL([isDeleted], 0) = 1 OR ISNULL([isBlackList], 0) = 1 OR statusesID <> 2 then 1 else 0 end blocked FROM sb_staff s join sb_staff_passwords p on p.login = s.username where s.username = @login";
            cmd.Parameters.AddWithValue("login", request.Username);
            using SqlDataReader dr =  await cmd.ExecuteReaderAsync();
            if (dr.Read())
            {
                idmUser.Id = (int)dr["id"];
                idmUser.Fio = (string)dr["fio"];
                idmUser.Username = (string)dr["username"];
                idmUser.Idm = (string)dr["idm"];
                idmUser.Password = (string)dr["password"];
                idmUser.Blocked = (int)dr["blocked"] == 1;
            }
        }
        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Username == request.Username);

        if (user == null)
        {
            // TODO: try to find user in idmnew database
            // if exists and active then copy to this database.
        }

        if (user == null || !user.IsActive || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
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

        // TODO: check if user is active in 

        // Обновляем данные пользователя
        user.LastLoginAt = DateTime.UtcNow;
        user.LastSeenAt = DateTime.UtcNow;
        _dbContext.Users.Update(user);
        await _dbContext.SaveChangesAsync();

        // Генерируем токены
        var userDto = new UserDto
        {
            id = user.Id,
            username = user.Username,
            display_name = user.DisplayName ?? user.Username,
            avatar_url = _urlResolver.ResolveUrl(user.AvatarUrl), 
            is_online = true, 
            last_seen_at = user.LastSeenAt
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

        return Ok(new LoginResultDto()
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
        if (string.IsNullOrEmpty(request?.refresh_token))
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
            .FirstOrDefaultAsync(rt => rt.Token == request.refresh_token);

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
            id = user.Id,
            username = user.Username,
            display_name = user.DisplayName ?? user.Username,
            avatar_url = user.AvatarUrl
        };

        var accessToken = GenerateAccessToken(userDto);
        var expiresIn = Convert.ToInt32(_config["Jwt:ExpiryMinutes"]) * 60;

        // Опционально: обновляем дату последнего использования refresh token
        refreshToken.CreatedAt = DateTime.UtcNow;
        _dbContext.RefreshTokens.Update(refreshToken);
        await _dbContext.SaveChangesAsync();

        return Ok(new RefreshResultDto()
        {
            access_token = accessToken,
            expires_in = expiresIn
        });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] RefreshRequest request)
    {
        if (string.IsNullOrEmpty(request?.refresh_token))
        {
            return NoContent();
        }

        // Ищем refresh token в БД
        var refreshToken = await _dbContext.RefreshTokens
            .Include(rt => rt.User)
            .FirstOrDefaultAsync(rt => rt.Token == request.refresh_token);

        if (refreshToken == null)
        {
            return NoContent();
        }

        // Обновляем данные пользователя
        refreshToken.User.LastSeenAt = DateTime.UtcNow;
        _dbContext.Users.Update(refreshToken.User);
        await _dbContext.SaveChangesAsync();

        refreshToken.RevokedAt = DateTime.UtcNow;
        _dbContext.RefreshTokens.Update(refreshToken);
        await _dbContext.SaveChangesAsync();

        return NoContent();
    }


    public class RefreshRequest
    {
        public string refresh_token { get; set; } = string.Empty;
    }
    
    public class UserDto
    {
        public Guid id { get; set; }
        public string username { get; set; } = string.Empty;
        public string display_name { get; set; } = string.Empty;
        public string? avatar_url { get; set; }
        public bool is_online { get; set; }
        public DateTime last_seen_at { get; set; }
    }

    private string GenerateAccessToken(UserDto user)
    {
        var claims = new[]
        {
        new Claim(ClaimTypes.NameIdentifier, user.id.ToString()),
        new Claim(ClaimTypes.Name, user.username),
        new Claim("display_name", user.display_name ?? user.username)
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
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public class LoginResultDto
    {
        public string access_token { get; set; }
        public string refresh_token { get; set; }
        public int expires_in { get; set; }
        public UserDto user { get; set; }
    }
    public class RefreshResultDto
    {
        public string access_token { get; set; }
        public int expires_in { get; set; }
    }
}


