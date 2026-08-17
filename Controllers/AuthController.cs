using Asp.Versioning;
using Google.Apis.Http;
using IDMChat.DTO;
using IDMChat.Models;
using IDMChat.Services;
using IDMChat.Utils;
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
    private readonly IIdmApiClient _idmClient;
    private readonly UserCache _userCache;
    private readonly IAuthContextService _authContextService;
    private readonly IServiceProvider _serviceProvider;

    public AuthController(ChatDbContext dbContext, IConfiguration config, IChatPathUrlResolver urlResolver, UserCache userCache, IIdmApiClient idmClient, IAuthContextService authContextService, IServiceProvider serviceProvider)
    {
        _dbContext = dbContext;
        _config = config;
        _urlResolver = urlResolver;
        _idmClient = idmClient;
        _userCache = userCache;
        _authContextService = authContextService;
        _serviceProvider = serviceProvider;
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
    public async Task<IActionResult> Login([FromBody] LoginRequest dto, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(dto.Username) || string.IsNullOrEmpty(dto.Password))
        {
            return Unauthorized(new
            {
                error = new ErrorDto()
                {
                    code = "INVALID_CREDENTIALS",
                    message = "Неверный логин или пароль"
                }
            });
        }

        var idmResult = await _idmClient.VerifyCredentialsAsync(dto.Username, dto.Password, ct);

        // 2. Если ИДМ вернула null (неверные учетные данные или заблокирован)
        if (idmResult == null)
        {
            return Unauthorized(new { error = new { code = "INVALID_CREDENTIALS", message = "Неверный логин или пароль, либо учетная запись заблокирована в ИДМ" } });
        }

        string incomingName = !string.IsNullOrWhiteSpace(idmResult.FullName) ? idmResult.FullName : dto.Username;

        var localUser = await _dbContext.Users
                .AsTracking()
                .FirstOrDefaultAsync(u => u.IdmUserId == idmResult.UserId, ct);

        if (localUser == null)
        {
            // КЕЙС А: Пользователь зашел в чат ВПЕРВЫЕ (Автоматическое создание)
            localUser = new User
            {
                Id = Guid.NewGuid(),
                IdmUserId = idmResult.UserId,
                DisplayName = incomingName,
                IsDisplayNameCustom = false, // Новое имя прилетело из ИДМ, флаг сброшен
                IdmRole = idmResult.Role,
                Role = MapIdmRoleToChatRole(idmResult.Role),
                idm = idmResult.CompanyCode, // Привязываем код компании сотрудника
                CreatedAt = DateTime.UtcNow,
                IsActive = true,
                Username = dto.Username
            };
            _dbContext.Users.Add(localUser);
            await _dbContext.SaveChangesAsync(ct);

            var defaultProfile = new UserProfile
            {
                UserId = localUser.Id,
                RoleId = null, // Будет настроено позже через админку, либо привяжите дефолтную роль
                DefaultSectionKey = null,
                ClubLandingSectionKey = null
            };
            _dbContext.UserProfiles.Add(defaultProfile);
        }
        else
        {
            // КЕЙС Б: Пользователь уже существует в чате
            // Обновляем DisplayName только если пользователь еще не менял его сам в чате (Защита кастомных имен)
            if (!localUser.IsDisplayNameCustom && localUser.DisplayName != incomingName)
            {
                localUser.DisplayName = incomingName;
            }

            // Роль и код компании (idm) в чате обновляем ВСЕГДА на основе мастер-системы ИДМ
            localUser.IdmRole = idmResult.Role;
            localUser.Role = MapIdmRoleToChatRole(idmResult.Role);
            localUser.idm = idmResult.CompanyCode;
        }

        await _dbContext.SaveChangesAsync(ct);
        _userCache.AddOrUpdateUser(localUser.Id, localUser.DisplayName, localUser.AvatarUrl, localUser.CustomStatus, localUser.LastSeenAt, localUser.IdmUserId, localUser.IsActive);


        // TODO: check if user is active in 
        if (!localUser.IsActive)
            return Unauthorized(new { error = new { code = "ACCOUNT_BLOCKED", message = "Учетная запись заблокирована." } });

        // Обновляем данные пользователя
        localUser.LastLoginAt = DateTime.UtcNow;
        localUser.LastSeenAt = DateTime.UtcNow;
        _dbContext.Users.Update(localUser);
        await _dbContext.SaveChangesAsync();

        var authContext = await _authContextService.GetContextAsync(localUser.Id, ct);

        if (localUser.IdmUserId.HasValue)
        {
            if (authContext.clubs != null && authContext.clubs.Count > 0)
            {
                // Используем Task.Run для Fire-and-Forget
                _ = Task.Run(async () =>
                {
                    using var scope = _serviceProvider.CreateScope();
                    var idmClient = scope.ServiceProvider.GetRequiredService<IIdmApiClient>();
                    var clubSyncService = scope.ServiceProvider.GetRequiredService<IClubSyncService>();

                    var idmClubs = await idmClient.GetUserClubsAsync(localUser.IdmUserId.Value, CancellationToken.None);
                    await clubSyncService.SyncUserClubsAsync(localUser.Id, idmClubs, CancellationToken.None);
                });
            }
            else
            {
                using var scope = _serviceProvider.CreateScope();
                var idmClient = scope.ServiceProvider.GetRequiredService<IIdmApiClient>();
                var clubSyncService = scope.ServiceProvider.GetRequiredService<IClubSyncService>();

                var idmClubs = await idmClient.GetUserClubsAsync(localUser.IdmUserId.Value, CancellationToken.None);
                await clubSyncService.SyncUserClubsAsync(localUser.Id, idmClubs, CancellationToken.None);
            }
        }

        // Генерируем токены
        var userDto = new UserDto
        {
            id = localUser.Id,
            username = localUser.Username,
            display_name = localUser.DisplayName ?? localUser.Username,
            avatar_url = _urlResolver.ResolveUrl(localUser.AvatarUrl), 
            is_online = true, 
            last_seen_at = localUser.LastSeenAt,

            role = authContext.user.role,
            //fullName = authContext.user.fullName,
            clubLandingKey = authContext.user.clubLandingKey,
            defaultSection = authContext.user.defaultSection != null ? new DefaultSectionDto
            {
                key = authContext.user.defaultSection.key,
                scope = authContext.user.defaultSection.scope
            } : null
        };

        var accessToken = GenerateAccessToken(userDto);
        var refreshToken = GenerateRefreshToken();
        var expiresIn = Convert.ToInt32(_config["Jwt:ExpiryMinutes"]) * 60;

        // Сохраняем refresh token в БД
        var refreshTokenEntity = new RefreshToken
        {
            Token = refreshToken,
            UserId = localUser.Id,
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
            user = userDto,
            permissions = authContext.permissions,
            limits = authContext.limits,
            clubs = authContext.clubs.Select(ClubMapper.ToFrontendDto).ToList(),

            menu = authContext.menu.Select(m => new MenuDto
            {
                key = m.key,
                scope = m.scope,
                title = m.title,
                icon = m.icon,
                order = m.order,
                children = m.children.Select(c => new MenuDto
                {
                    key = c.key,
                    scope = m.scope, // ⚠️ Дочерние листья наследуют scope родителя по ТЗ
                    title = c.title,
                    icon = c.icon,
                    order = c.order,
                    children = new List<MenuDto>() // Глубже 1 уровня клиент дерево не строит
                }).ToList()
            }).ToList()
        });
    }

    public static class ClubMapper
    {
        public static ClubDto ToFrontendDto(IdmClubDto idmClub) => new ClubDto
        {
            id = idmClub.Id,
            bbID = idmClub.Code, // p.bbID из ИДМ
            name = idmClub.Name,
            city = new CityDto { name = idmClub.CityName, gmt = idmClub.CityGmt }
        };
        public static ClubDto ToFrontendDto(ThinClubDto idmClub) => new ClubDto
        {
            id = idmClub.id,
            bbID = idmClub.bbid, // p.bbID из ИДМ
            name = idmClub.name,
            city = new CityDto { name = idmClub.city.name, gmt = idmClub.city.gmt }
        };
    }

    private UserRole MapIdmRoleToChatRole(string idmRole)
    {
        if (string.IsNullOrEmpty(idmRole)) return UserRole.Employee;

        // Приводим к нижнему регистру для защиты от опечаток
        return idmRole.ToLowerInvariant() switch
        {
            "administrator" => UserRole.Admin, // Главный админ ИДМ становится админом чата
            "manager" => UserRole.Manager,
            "pointmanager" => UserRole.Manager,                                  // "hr_manager" => UserRole.Admin, // Пример: если в будущем захотите добавить еще админов
            _ => UserRole.Employee // Все остальные (pointmanager, operator и т.д.) для чата пока обычные пользователи
        };
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request)
    {
        if (string.IsNullOrEmpty(request?.refresh_token))
        {
            return Unauthorized(new
            {
                error = new ErrorDto()
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
                error = new ErrorDto
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
                error = new ErrorDto
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
                error = new ErrorDto
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
                error = new ErrorDto
                {
                    code = "USER_INACTIVE",
                    message = "Пользователь неактивен"
                }
            });
        }

        user.LastSeenAt = DateTime.UtcNow;
        _dbContext.Users.Update(user);

        // Создаем новый access token
        var userDto = new UserDto
        {
            id = user.Id,
            username = user.Username,
            display_name = user.DisplayName ?? user.Username,
            avatar_url = user.AvatarUrl, 
            custom_status = user.CustomStatus, 
            last_seen_at = user.LastSeenAt, 
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

}


