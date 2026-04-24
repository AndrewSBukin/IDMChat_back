using Asp.Versioning;
using IDMChat.Domain;
using IDMChat.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IDMChat.Controllers
{
    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    [ApiVersion("1.0")]
    public class MessageController : ControllerBase
    {
        private readonly MessageService _messageService;

        public MessageController(MessageService messageService)
        {
            _messageService = messageService;
        }

        [HttpPost("send")]
        public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest request)
        {
            var message = await _messageService.SendMessageAsync(
                request.UserId,
                request.Message,
                request.ChannelId);

            return Ok(new
            {
                success = true,
                messageId = message.Id,
                createdAt = message.CreatedAt
            });
        }
    }

    public record SendMessageRequest(
        int UserId,
        string Message,
        int ChannelId
    );
}
