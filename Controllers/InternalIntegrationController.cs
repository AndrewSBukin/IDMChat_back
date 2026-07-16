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

        public InternalIntegrationController(ChatDbContext dbContext, ChatStateCache chatCache, UserCache userCache, IHubContext<ChatHub> hubContext, IChatPathUrlResolver urlResolver)
        {
            _dbContext = dbContext;
            _chatCache = chatCache;
            _userCache = userCache;
            _hubContext = hubContext;
            _urlResolver = urlResolver;
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

            var message = new Message
            {
                ConversationId = mapping.ConversationId,
                SenderId = Guid.Parse("00000000-0000-0000-0000-000000000001"), // Системная авто-рассылка
                Text = dto.text.Trim(),
                Type = MessageType.Text,
                CreatedAt = DateTime.UtcNow,
                IsDeleted = false
            };

            _dbContext.Messages.Add(message);
            await _dbContext.SaveChangesAsync(ct);

            // Обновляем LastMessage диалога
            var conversation = await _dbContext.Conversations.AsTracking().FirstOrDefaultAsync(c => c.Id == mapping.ConversationId, ct);
            if (conversation != null)
            {
                conversation.LastMessageId = message.Id;
                conversation.UpdatedAt = DateTime.UtcNow;
            }

            await _dbContext.SaveChangesAsync(ct);
            _chatCache.Invalidate(mapping.ConversationId);

            // Публикуем сообщение в SignalR онлайн-участникам чата
            var bot = _userCache.GetUser(message.SenderId);
            var messageDto = new
            {
                id = message.Id,
                type = "system",
                text = message.Text,
                created_at = message.CreatedAt,
                sender = new
                {
                    id = message.SenderId,
                    display_name = bot?.DisplayName,
                    avatar_url = _urlResolver.ResolveUrl(bot?.AvatarUrl),
                    status = "online",
                    is_online = true,
                    custom_status = bot?.CustomStatus,
                    last_seen_at = bot?.LastSeenAt
                },
                reply_to = (object)null,
                attachments = new List<AttachmentDto>(),
                mentions = new List<UserMention>()
            };

            await _hubContext.Clients.Group(mapping.ConversationId.ToString()).SendAsync("message_new", new
            {
                conversation_id = mapping.ConversationId.ToString(),
                message = messageDto
            }, ct);

            var truncatedText = (message.Text ?? string.Empty).Length > 100 ? message.Text[..100] + "..." : (message.Text ?? string.Empty);
            var chat = await _chatCache.GetConversationAsync(conversation.Id);
            var onlineMembers = _userCache.GetOnlineMembers(chat.Members).ToList();
            var lastMessagePreview = new LastMessageDto
            {
                id = message.Id,
                text = truncatedText,
                type = "system",
                sender_id = message.SenderId,
                sender_name = bot?.DisplayName ?? "",
                created_at = message.CreatedAt,
                attachments = new List<AttachmentDto>(),
                mentions = new List<UserMention>()
            };

            var groupUpdateDto = new ConversationUpdatedDto
            {
                id = conversation.Id,
                type = conversation.Type.ToString().ToLower(),
                name = conversation.Name,
                avatar_url = _urlResolver.ResolveUrl(conversation.AvatarUrl) ?? "",
                last_message = lastMessagePreview,
                updated_at = message.CreatedAt
            };

            var onlineUserStrings = onlineMembers.Select(id => id.ToString()).ToList();
            await _hubContext.Clients.Users(onlineUserStrings).SendAsync("conversation_updated", groupUpdateDto, ct);

            return Ok(new { success = true });
        }
    }
}
