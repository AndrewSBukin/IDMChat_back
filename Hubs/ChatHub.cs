using IDMChat.Controllers;
using IDMChat.Models;
using IDMChat.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
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
            // Пользователь подключился
            var userId = Context.GetUserId();
            _userCache.AddConnection(userId, Context.ConnectionId);

            // Отправляем все непрочитанные сообщения
            var unreadData = await _db.ConversationMembers
                .Where(cm => cm.UserId == userId && cm.UnreadCount > 0)
                .Select(cm => new
                {
                    cm.ConversationId,
                    cm.UnreadCount,
                    Messages = _db.Messages
                        .Where(m => m.ConversationId == cm.ConversationId && m.Id > (cm.LastReadMessageId ?? 0))
                        .OrderBy(m => m.Id)
                        .Take(50)
                        .Select(m => new
                        {
                            m.Id,
                            m.Text,
                            m.SenderId,
                            m.CreatedAt
                        })
                        .ToList()
                })
                .ToListAsync();

            foreach (var item in unreadData)
            {
                await Clients.Caller.SendAsync("UnreadMessages", item.ConversationId, item.Messages);
                await Clients.Caller.SendAsync("UnreadCountUpdated", item.ConversationId, item.UnreadCount);
            }

            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            // Пользователь отключился
            await base.OnDisconnectedAsync(exception);
        }

        public async Task SendMessage(Guid conversationId, string text, Guid tempId)
        {
            var userId = Context.GetUserId();

            try
            {
                // 1. Дедупликация
                var exists = await _db.Messages
                    .AnyAsync(m => m.ConversationId == conversationId && m.ClientTempId == tempId);

                if (exists)
                {
                    await Clients.Caller.SendAsync("MessageDuplicate", tempId);
                    return;
                }

                // 2. Проверки из кэша
                var chat = await _chatCache.GetConversationAsync(conversationId);

                if (!chat.IsMember(userId))
                    throw new HubException("NOT_MEMBER");

                if (chat.IsWriteRestricted && !chat.IsAdmin(userId))
                    throw new HubException("ONLY_ADMINS_CAN_WRITE");

                // 3. Создание сообщения
                var message = new Message
                {
                    ClientTempId = tempId,
                    ConversationId = conversationId,
                    SenderId = userId,
                    Text = text,
                    Type = MessageType.Text,
                    SentAt = DateTime.UtcNow,
                    ChannelId = 0
                };

                var truncatedText = text.Length > 100 ? text[..100] + "..." : text;

                // 4. Сохранение в БД
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

                // 5. Обновление кэша
                _chatCache.UpdateLastMessage(conversationId, message, truncatedText);
                _chatCache.IncrementUnreadCounts(conversationId, userId);

                // 6. Подтверждение отправителю
                await Clients.Caller.SendAsync("MessageConfirmed", message.Id, tempId);

                // 7. Рассылка остальным
                var messageDto = new
                {
                    message.Id,
                    message.Text,
                    SenderId = userId,
                    message.CreatedAt
                };

                var onlineMembers = _userCache.GetOnlineMembers(chat.Members);

                foreach (var memberId in onlineMembers.Where(m => m != userId))
                {
                    var connectionId = _userCache.GetConnectionId(memberId);
                    if (connectionId != null)
                    {
                        await Clients.Client(connectionId).SendAsync("NewMessage", conversationId, messageDto);
                        var newUnreadCount = chat.GetUnreadCount(memberId);
                        await Clients.Client(connectionId).SendAsync("UnreadCountUpdated", conversationId, newUnreadCount);
                    }
                }

                _logger.LogDebug("Message {MessageId} sent to conversation {ConversationId} by {UserId}",
                    message.Id, conversationId, userId);
            }
            catch (HubException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending message to {ConversationId}", conversationId);
                throw new HubException("MESSAGE_SEND_FAILED");
            }
        }

        public async Task JoinChat(string chatId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, chatId);
        }

        public async Task LeaveChat(string chatId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, chatId);
        }
    }
}
