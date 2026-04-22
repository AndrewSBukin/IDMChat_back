using Asp.Versioning;
using IDMChat.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IDMChat.Controllers
{
    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    [Authorize]
    [ApiVersion("1.0")]
    public class ChatController : ControllerBase
    {
        private readonly ILogger<ChatController> _logger;
        private readonly ChatDbContext _dbContext;

        public ChatController(ChatDbContext dbContext, ILogger<ChatController> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        [HttpGet("messages/{chatId}")]
        public async Task<IActionResult> GetMessages(Guid chatId, int count = 50)
        {
            var messages = await _dbContext.Messages
                .Where(m => m.ChatId == chatId && !m.IsDeleted)
                .OrderByDescending(m => m.SentAt)
                .Take(count)
                .ToListAsync();

            return Ok(messages);
        }

        [HttpGet("health")]
        public IActionResult Health()
        {
            return Ok(new { status = "OK", timestamp = DateTime.UtcNow });
        }
    }
}
