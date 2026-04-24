using Asp.Versioning;
using BCrypt.Net;
using IDMChat.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.AspNetCore.Mvc;
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
    private readonly ChatDbContext _dbContext;
    private readonly IConfiguration _config;

    public UsersController(ChatDbContext dbContext, IConfiguration config)
    {
        _dbContext = dbContext;
        _config = config;
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


    [HttpGet("")]
    public async Task<IActionResult> GetUsers()
    {
        var users = await _dbContext.Users
            .Select(u => new UserDto
            {
                id = u.Id,
                username = u.Username,
                display_name = string.IsNullOrEmpty(u.DisplayName) ? u.Username : u.DisplayName,
                avatar_url = u.AvatarUrl,
                is_online = u.IsOnline,
                last_seen_at = u.LastSeenAt
            })
            .ToListAsync();

        return Ok(users);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetUser(Guid id)
    {
        var user = await _dbContext.Users
            .Where(u => u.Id == id)
            .Select(u => new UserDto
            {
                id = u.Id,
                username = u.Username,
                display_name = string.IsNullOrEmpty(u.DisplayName) ? u.Username : u.DisplayName,
                avatar_url = u.AvatarUrl,
                is_online = u.IsOnline,
                last_seen_at = u.LastSeenAt
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

