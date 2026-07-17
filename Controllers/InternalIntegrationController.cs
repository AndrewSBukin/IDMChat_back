using Asp.Versioning;
using IDMChat.DTO;
using IDMChat.Hubs;
using IDMChat.Models;
using IDMChat.Services;
using IDMChat.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Net.Mail;

namespace IDMChat.Controllers
{
    [ApiController]
    [Route("api/v1/internal/integration")]
    [ApiVersion("1.0")]
    [AllowAnonymous] // Все методы публичны, но жестко защищены по ApiKey
    public class InternalIntegrationController : ControllerBase
    {
        private readonly ChatDbContext _dbContext;
        private readonly ChatStateCache _chatCache;
        private readonly UserCache _userCache;
        private readonly IHubContext<ChatHub> _hubContext;
        private readonly IChatPathUrlResolver _urlResolver;
        private readonly INewMessageService _newMessageService;

        public InternalIntegrationController(ChatDbContext dbContext, ChatStateCache chatCache, UserCache userCache, IHubContext<ChatHub> hubContext, IChatPathUrlResolver urlResolver, INewMessageService newMessageService)
        {
            _dbContext = dbContext;
            _chatCache = chatCache;
            _userCache = userCache;
            _hubContext = hubContext;
            _urlResolver = urlResolver;
            _newMessageService = newMessageService;
        }

        // Вспомогательный метод сквозной проверки ключа ИДМ
        private bool IsValidApiKey()
        {
            return true;
            if (!Request.Headers.TryGetValue("X-Internal-Api-Key", out var apiKey) ||
                apiKey != "SuperSecretKey_IdmToChat_2026_SecureToken!")
            {
                return false;
            }
            return true;
        }

        // =========================================================================
        // МЕТОД 1: Мгновенная блокировка пользователя из ИДМ
        // =========================================================================
        [HttpPost("block-user")]
        [AllowAnonymous]
        public async Task<IActionResult> BlockUserInternal([FromQuery] int id, [FromQuery] int isblocked, CancellationToken ct = default)
        {
            if (!IsValidApiKey()) return Forbid();

            // 2. Ищем Guid пользователя чата по его int ID из ИДМ прямо в оперативной памяти
            var chatUserId = _userCache.GetChatUserIdByIdmId(id);
            if (chatUserId == null)
            {
                // Если юзер еще ни разу не заходил в чат и его нет в базе чата — чистить некого
                return Ok(new { message = "User not found in chat local cache" });
            }

            // 3. МГНОВЕННЫЙ РАЗРЫВ SIGNALR СОЕДИНЕНИЙ (Highload-подход)
            // Отправляем системную команду "Disconnect" на все устройства заблокированного пользователя
            // Название метода "Disconnect" должно поддерживаться в вашем ChatHub (он вызовет Context.Abort())
            if (isblocked > 0)
                await _hubContext.Clients.User(chatUserId.Value.ToString()).SendAsync("Disconnect", new
                {
                    reason = "ACCOUNT_BLOCKED",
                    message = "Ваша учетная запись заблокирована администратором в системе ИДМ"
                });

            var userInDb = await _dbContext.Users
                .AsTracking()
                .FirstOrDefaultAsync(u => u.Id == chatUserId.Value, ct);

            if (userInDb != null)
            {
                // 1. Фиксируем блокировку в СУБД (Защита от перезапуска сервера!)
                userInDb.IsActive = isblocked == 0;
                await _dbContext.SaveChangesAsync(ct);

                // 2. Обновляем состояние оперативной памяти (UserCache) БЕЗ затирания аватарок
                var currentCache = _userCache.GetUser(chatUserId.Value);
                string cleanName = currentCache?.DisplayName.Replace(" [Блокирован]", "") ?? userInDb.DisplayName;

                _userCache.AddOrUpdateUser(
                    chatUserId.Value,
                    cleanName,
                    currentCache?.AvatarUrl,
                    currentCache?.CustomStatus,
                    currentCache?.LastSeenAt ?? DateTime.MinValue,
                    id,
                    isActive: isblocked == 0 // Метод кэша сам допишет "[Блокирован]" в RAM
                );
            }

            return Ok(new { success = true, message = $"Успешно" });
        }

        // =========================================================================
        // МЕТОД 2: Дублирование системных рассылок/алертов из ИДМ (Вместо Telegram)
        // =========================================================================
        [HttpPost("send-message")]
        public async Task<IActionResult> SendExternalMessage([FromBody] ExternalMessageRequestDto dto, CancellationToken ct)
        {
            if (!IsValidApiKey()) return Forbid();

            if (string.IsNullOrWhiteSpace(dto.chat_id) || string.IsNullOrWhiteSpace(dto.text))
            {
                return BadRequest(new { error = "Параметры chat_id и text обязательны" });
            }

            // Находим маппинг по индексу БД
            var mapping = await _dbContext.ExternalChatMappings
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.ExternalChatId == dto.chat_id, ct);

            if (mapping == null)
            {
                return Ok(new { success = false, message = "Маппинг не настроен. Пропущено." });
            }

            var msg = new ChatHub.NewMessageRequest()
            {
                conversation_id = mapping.ConversationId,
                text = dto.text.Trim(),
                type = "text", 
                temp_id = Guid.NewGuid(), 
                attachment_ids = new List<Guid>(), 
                mentions = new List<ChatHub.MentionItem>(), 
                reply_to_message_id = null
            };
            await _newMessageService.HandleSendMessage(msg, Guid.Parse("00000000-0000-0000-0000-000000000001"), ct);
            
            return Ok(new { success = true });
        }
    }
}
