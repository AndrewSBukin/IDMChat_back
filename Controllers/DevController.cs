using Asp.Versioning;
using IDMChat.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IDMChat.Controllers
{
    [Route("api/v{version:apiVersion}/[controller]")]
    [ApiController]
    [ApiVersion("1.0")]
    //[AllowAnonymous]
    public class DevController : ControllerBase
    {
        private readonly ChatDbContext _dbContext;

        public DevController(ChatDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        [HttpGet("health")]
        public IActionResult Health()
        {
            return Ok(new { status = "OK", timestamp = DateTime.UtcNow });
        }

        [HttpPost("create-user")]
        public async Task<IActionResult> DebugCreateUser([FromBody] DebugCreateUserRequest request)
        {
            var exists = await _dbContext.Users
                .AnyAsync(u => u.Username == request.Username);

            if (exists)
            {
                return BadRequest(new
                {
                    error = new
                    {
                        code = "USER_EXISTS",
                        message = "Пользователь уже существует"
                    }
                });
            }

            var user = new User
            {
                Id = Guid.NewGuid(),
                Username = request.Username,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
                DisplayName = request.DisplayName ?? request.Username,
                AvatarUrl = request.AvatarUrl,
                ConnectionId = string.Empty,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            _dbContext.Users.Add(user);
            await _dbContext.SaveChangesAsync();

            return Ok(new
            {
                id = user.Id,
                username = user.Username,
                displayName = user.DisplayName,
                message = "Пользователь создан"
            });
        }
        public class DebugCreateUserRequest
        {
            public string Username { get; set; } = string.Empty;
            public string Password { get; set; } = string.Empty;
            public string? DisplayName { get; set; }
            public string? AvatarUrl { get; set; }
        }
    }
}
