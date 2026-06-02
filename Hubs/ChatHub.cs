using IDMChat.Controllers;
using IDMChat.DTO;
using IDMChat.Models;
using IDMChat.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.RegularExpressions;

namespace IDMChat.Hubs
{
    [Authorize]
    public class ChatHub : Hub
    {
        private readonly ChatDbContext _db;
        private readonly ChatStateCache _chatCache;
        private readonly UserCache _userCache;
        private readonly ILogger<ChatHub> _logger;

        public ChatHub(ChatDbContext dbContext, ChatStateCache chatCache, UserCache userCache, ILogger<ChatHub> logger)
        {
            _db = dbContext;
            _chatCache = chatCache;
            _userCache = userCache;
            _logger = logger;
        }

        public override async Task OnConnectedAsync()
        {
            var userId = Context.GetUserId();
            _userCache.AddConnection(userId, Context.ConnectionId);

            // 1. Добавляем в группы
            var userChats = await _db.ConversationMembers
                .Where(cm => cm.UserId == userId)
                .Select(cm => new { cm.ConversationId, cm.UnreadCount })
                .ToListAsync();

            foreach (var chat in userChats)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, chat.ConversationId.ToString());
            }

            // 2. Отправляем ТОЛЬКО счётчики непрочитанных
            var unreadSummary = userChats
                .Where(c => c.UnreadCount > 0)
                .ToDictionary(c => c.ConversationId, c => c.UnreadCount);

            await Clients.Caller.SendAsync("unread_summary", unreadSummary);

            // 3. Сообщения подгружаются по мере открытия чатов (через REST)
            await Clients.All.SendAsync("user_status", new { user_id = userId, status = "online" });

            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var userId = Context.GetUserId();

            // 1. Уведомить всех об оффлайн статусе
            await Clients.All.SendAsync("user_status", new { user_id = userId, status = "offline" });

            // 2. Удалить пользователя из кэша SignalR групп (если не удаляются автоматически)
            var userChats = await _db.ConversationMembers
                .Where(cm => cm.UserId == userId)
                .Select(cm => cm.ConversationId.ToString())
                .ToListAsync();

            foreach (var chatId in userChats)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, chatId);
            }

            // 3. Очистить ConnectionId в UserCache
            _userCache.RemoveConnection(userId, Context.ConnectionId);

            // 4. Обновить статус в БД (опционально)
            var user = await _db.Users.FindAsync(userId);
            if (user != null)
            {
                user.IsOnline = false;
                user.LastSeenAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }

            await base.OnDisconnectedAsync(exception);
        }

        /// <summary>
        /// Отправить сообщение в чат
        /// </summary>
        /// <param name="conversationId">ID чата</param>
        /// <param name="text">Текст сообщения</param>
        /// <param name="tempId">Временный ID клиента для дедупликации</param>
        /// <param name="replyToMessageId">ID сообщения, на которое отвечаем</param>
        /// <param name="type">Тип сообщения: text, image, video, file, voice</param>
        /// <param name="attachmentIds">Список ID загруженных файлов</param>
        public async Task SendMessage(Guid conversationId, string text, Guid tempId, long? replyToMessageId = null, string type = "text", List<Guid>? attachmentIds = null)
        {
            var userId = Context.GetUserId();
            try
            {
                // 1. Дедупликация
                var exists = await _db.Messages
                    .AnyAsync(m => m.ConversationId == conversationId && m.ClientTempId == tempId);

                if (exists)
                {
                    await Clients.Caller.SendAsync("message_duplicate", tempId);
                    return;
                }

                // 2. Проверки из кэша
                var chat = await _chatCache.GetConversationAsync(conversationId);

                if (!chat.IsMember(userId))
                    throw new HubException("NOT_MEMBER");

                if (chat.IsWriteRestricted && !chat.IsAdmin(userId))
                    throw new HubException("ONLY_ADMINS_CAN_WRITE");

                // 3. Валидация параметров
                var messageType = ParseMessageType(type);

                object? replyToObj = null;
                if (replyToMessageId.HasValue)
                {
                    var replyMessage = await _db.Messages
                        .Include(m => m.Sender)
                        .FirstOrDefaultAsync(m => m.Id == replyToMessageId
                                                    && m.ConversationId == conversationId
                                                    && !m.IsDeleted);
                    if (replyMessage == null)
                        throw new HubException("REPLY_TO_MESSAGE_NOT_FOUND");

                    replyToObj = new
                    {
                        id = replyMessage.Id,
                        sender_id = replyMessage.SenderId,
                        sender_name = replyMessage.Sender.DisplayName,
                        text = replyMessage.Text.Length > 100
                                ? replyMessage.Text[..100] + "..."
                                : replyMessage.Text,
                        type = replyMessage.Type.ToString().ToLower()
                    };
                }

                // 4. Создание сообщения
                var message = new Message
                {
                    ClientTempId = tempId,
                    ConversationId = conversationId,
                    SenderId = userId,
                    Text = text ?? string.Empty,
                    Type = MessageType.Text,
                    ReplyToMessageId = replyToMessageId,
                    SentAt = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow,
                    ChannelId = 0
                };

                // 5. Привязка вложений
                if (attachmentIds != null && attachmentIds.Any())
                {
                    var attachments = await _db.FileAttachments.AsTracking()
                        .Where(a => attachmentIds.Contains(a.Id) && a.UserId == userId)
                        .ToListAsync();

                    foreach (var attachment in attachments)
                    {
                        attachment.MessageId = message.Id;
                    }
                }

                var truncatedText = (text ?? string.Empty).Length > 100 ? text[..100] + "..." : (text ?? string.Empty);

                // 6. Сохранение в БД
                _db.Messages.Add(message);

                var conversation = await _db.Conversations.FindAsync(conversationId);
                conversation.LastMessageId = message.Id;
                conversation.LastMessageText = truncatedText;
                conversation.LastMessageSenderId = userId;
                conversation.LastMessageCreatedAt = message.CreatedAt;
                conversation.UpdatedAt = message.CreatedAt;

                var members = await _db.ConversationMembers
                    .Where(cm => cm.ConversationId == conversationId && cm.UserId != userId)
                    .ToListAsync();

                foreach (var member in members)
                    member.UnreadCount++;

                await _db.SaveChangesAsync(); // message.Id заполняется

                // 7. Обновление кэша
                _chatCache.UpdateLastMessage(conversationId, message, truncatedText);
                _chatCache.IncrementUnreadCounts(conversationId, userId);

                // 8. Подтверждение отправителю
                await Clients.Caller.SendAsync("message_confirmed", message.Id, tempId);

                // 9. Рассылка остальным
                var messageDto = new
                {
                    message.Id,
                    message.Text,
                    sender_id = userId,
                    created_at = message.CreatedAt,
                    type = type,
                    reply_to_id = replyToMessageId,
                    reply_to = replyToObj,
                    attachment_ids = attachmentIds
                };

                var onlineMembers = _userCache.GetOnlineMembers(chat.Members);

                foreach (var memberId in onlineMembers.Where(m => m != userId))
                {
                    var connectionId = _userCache.GetConnectionId(memberId);
                    if (connectionId != null)
                    {
                        // Отправляем сообщение получателю
                        await Clients.Client(connectionId).SendAsync("message_new", conversationId, messageDto);

                        // Уведомляем отправителя о доставке получателю
                        await Clients.Caller.SendAsync("message_delivered", new
                        {
                            messageId = message.Id,
                            userId = memberId
                        });

                        // Обновляем счетчик непрочитанных у получателя
                        var newUnreadCount = chat.GetUnreadCount(memberId);
                        await Clients.Client(connectionId).SendAsync("unread_count_updated", conversationId, newUnreadCount);
                    }
                }

                _logger.LogDebug("Message {MessageId} sent to conversation {ConversationId} by {UserId}", message.Id, conversationId, userId);
            }
            catch (HubException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending message to {ConversationId} {message}", conversationId, ex.Message);
                throw new HubException("MESSAGE_SEND_FAILED", ex);
            }
        }

        private MessageType ParseMessageType(string type)
        {
            return type?.ToLower() switch
            {
                "text" => MessageType.Text,
                "image" => MessageType.Image,
                "video" => MessageType.Video,
                "file" => MessageType.File,
                "voice" => MessageType.Voice,
                _ => MessageType.Text
            };
        }

        public async Task JoinConversation(Guid conversationId)
        {
            var userId = Context.GetUserId();

            // Проверяем, что пользователь участник чата
            var isMember = await _db.ConversationMembers
                .AnyAsync(cm => cm.ConversationId == conversationId && cm.UserId == userId);

            if (!isMember)
                throw new HubException("NOT_MEMBER");

            await Groups.AddToGroupAsync(Context.ConnectionId, conversationId.ToString());

            // Отправляем последние непрочитанные сообщения (только для этого чата)
            var member = await _db.ConversationMembers
                .FirstOrDefaultAsync(cm => cm.ConversationId == conversationId && cm.UserId == userId);

            if (member?.UnreadCount > 0)
            {
                var unreadMessages = await _db.Messages
                    .Where(m => m.ConversationId == conversationId
                                && m.Id > (member.LastReadMessageId ?? 0)
                                && !m.IsDeleted)
                    .OrderBy(m => m.Id)
                    .Take(50)
                    .Select(m => new MessageDto
                    {
                        Id = m.Id,
                        ConversationId = m.ConversationId,
                        SenderId = m.SenderId,
                        Sender = new UserBriefDto
                        {
                            Id = m.Sender.Id,
                            DisplayName = m.Sender.DisplayName,
                            AvatarUrl = m.Sender.AvatarUrl
                        },
                        Type = m.Type.ToString().ToLower(),
                        Text = m.Text,
                        CreatedAt = m.CreatedAt
                    })
                    .ToListAsync();

                await Clients.Caller.SendAsync("unread_messages", new
                {
                    conversation_id = conversationId,
                    messages = unreadMessages
                });

                // Сбрасываем счётчик непрочитанных в кэше
                _chatCache.ResetUnreadCount(conversationId, userId);
            }

            _logger.LogDebug("User {UserId} joined conversation {ConversationId}", userId, conversationId);
        }

        public async Task LeaveConversation(Guid conversationId)
        {
            var userId = Context.GetUserId();
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, conversationId.ToString());
            _logger.LogDebug("User {UserId} left conversation {ConversationId}", userId, conversationId);
        }

        public async Task SendTyping(Guid conversationId)
        {
            var userId = Context.GetUserId();

            // Проверяем, что пользователь участник чата (быстрая проверка через кэш)
            var chat = await _chatCache.GetConversationAsync(conversationId);
            if (!chat.IsMember(userId))
                return; // тихо игнорируем

            await Clients.Group(conversationId.ToString()).SendAsync("typing_start", new
            {
                conversation_id = conversationId,
                user_id = userId,
                user_name = Context.User?.FindFirst(ClaimTypes.Name)?.Value ?? userId.ToString()
            });
        }

        public async Task StopTyping(Guid conversationId)
        {
            var userId = Context.GetUserId();

            var chat = await _chatCache.GetConversationAsync(conversationId);
            if (!chat.IsMember(userId))
                return;

            await Clients.Group(conversationId.ToString()).SendAsync("typing_stop", new
            {
                conversation_id = conversationId,
                user_id = userId
            });
        }
    }
}
