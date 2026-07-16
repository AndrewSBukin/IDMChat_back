using Asp.Versioning;
using BCrypt.Net;
using IDMChat.DTO;
using IDMChat.Hubs;
using IDMChat.Middleware;
using IDMChat.Models;
using IDMChat.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
[Authorize]
[ApiVersion("1.0")]
public class UsersController : ControllerBase
{
    private readonly IConfiguration _config; 
    private readonly ChatDbContext _dbContext;
    private readonly ChatStateCache _chatCache;
    private readonly UserCache _userCache;
    private readonly IHubContext<ChatHub> _hubContext;

    public UsersController(ChatDbContext dbContext, IConfiguration configuration, ChatStateCache chatCache, UserCache userCache, IHubContext<ChatHub> hubContext)
    {
        _dbContext = dbContext;
        _config = configuration;
        _chatCache = chatCache;
        _userCache = userCache;
        _hubContext = hubContext;
    }


    [HttpGet("")]
    public async Task<IActionResult> GetUsers([FromQuery] string search = "", [FromQuery] int limit = 50, [FromQuery] int offset = 0, CancellationToken ct = default)
    {
        var currentUser = HttpContext.GetCurrentUser();
        var allusers = _dbContext.Users
            .Where(u => u.idm == currentUser.idm || currentUser.Role == UserRole.Admin)
            .Where(u => search == "" || u.Username.Contains(search) || u.Email.Contains(search) || u.DisplayName.Contains(search));

        var users = await allusers
            .Skip(offset)
            .Take(limit)
            .Select(u => new UserDto
            {
                id = u.Id,
                username = u.Username,
                display_name = string.IsNullOrEmpty(u.DisplayName) ? u.Username : u.DisplayName,
                status = _userCache.IsOnline(u.Id) ? "online" : "offline",
                avatar_url = u.AvatarUrl,
                is_online = _userCache.IsOnline(u.Id),
                last_seen_at = u.LastSeenAt,
                custom_status = u.CustomStatus
            })
            .ToListAsync();

        return Ok(new UsersDto(){ users = users, total = allusers.Count() });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetUser(Guid id)
    {
        var currentUser = HttpContext.GetCurrentUser();
        var user = await _dbContext.Users
            .Where(u => u.Id == id)
            .Select(u => new UserDto
            {
                id = u.Id,
                username = u.Username,
                display_name = string.IsNullOrEmpty(u.DisplayName) ? u.Username : u.DisplayName,
                status = _userCache.IsOnline(u.Id) ? "online" : "offline",
                avatar_url = u.AvatarUrl,
                is_online = _userCache.IsOnline(u.Id),
                last_seen_at = u.LastSeenAt,
                custom_status = u.CustomStatus
            })
            .FirstOrDefaultAsync();

        if (user == null)
            return NotFound(new {
                error = new {
                    code = "USER_NOT_FOUND",
                    message = "Пользователь не найден"
                }
            });

        return Ok(user);
    }

}

