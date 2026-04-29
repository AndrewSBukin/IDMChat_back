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
    public class ConversationController : ControllerBase
    {
        private readonly ILogger<ConversationController> _logger;
        private readonly ChatDbContext _dbContext;

        public ConversationController(ChatDbContext dbContext, ILogger<ConversationController> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        [HttpGet("messages/{conversationId}")]
        public async Task<IActionResult> GetMessages(Guid conversationId, int count = 50)
        {
            var messages = await _dbContext.Messages
                .Where(m => m.ConversationId == conversationId && !m.IsDeleted)
                .OrderByDescending(m => m.SentAt)
                .Take(count)
                .ToListAsync();

            return Ok(messages);
        }
    }
}
