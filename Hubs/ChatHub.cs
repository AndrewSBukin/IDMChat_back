using Azure.Core;
using IDMChat.Controllers;
using IDMChat.Domain;
using IDMChat.DTO;
using IDMChat.Models;
using IDMChat.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Net.Mail;
using System.Security.Claims;
using System.Text.RegularExpressions;
using static System.Runtime.InteropServices.JavaScript.JSType;

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

            // Загружаем данные пользователя для статуса
            var user = await _db.Users
                .Where(u => u.Id == userId)
                .Select(u => new
                {
                    u.Id,
                    u.DisplayName,
                    u.AvatarUrl,
                    u.CustomStatus
                })
                .FirstOrDefaultAsync();

            if (user != null)
            {
                _userCache.AddOrUpdateUser(userId, user.DisplayName);

                var now = DateTime.UtcNow;
                await _db.Users
                    .Where(u => u.Id == userId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(u => u.LastSeenAt, now));

                await Clients.All.SendAsync("user_status", new
                {
                    id = user.Id,
                    display_name = user.DisplayName,
                    avatar_url = user.AvatarUrl,
                    status = "online",           // "online", "offline", "away"
                    custom_status = user.CustomStatus,
                    is_online = true,
                    last_seen_at = now
                });
            }

            var userChats = await _db.ConversationMembers
                .Where(cm => cm.UserId == userId && cm.UnreadCount > 0)
                .Select(cm => new { cm.ConversationId, cm.UnreadCount })
                .ToListAsync();

            //foreach (var chat in userChats)
            //{
            //    await Groups.AddToGroupAsync(Context.ConnectionId, chat.ConversationId.ToString());
            //}

            // 2. Отправляем ТОЛЬКО счётчики непрочитанных
            var unreadSummary = userChats.ToDictionary(c => c.ConversationId, c => c.UnreadCount);

            await Clients.Caller.SendAsync("unread_summary", unreadSummary);


            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var userId = Context.GetUserId();

            var now = DateTime.UtcNow;

            var user = await _db.Users.FindAsync(userId);
            // 1. Уведомить всех об оффлайн статусе
            await Clients.All.SendAsync("user_status", new
            {
                id = user.Id,
                display_name = user.DisplayName,
                avatar_url = user.AvatarUrl,
                status = "offline",           // "online", "offline", "away"
                custom_status = user.CustomStatus,
                is_online = false,
                last_seen_at = now
            });

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
            if (user != null)
            {
                user.IsOnline = false;
                user.LastSeenAt = now;
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
        public async Task SendMessage0(Guid conversationId, string text, Guid tempId, long? replyToMessageId = null, string type = "text", List<Guid>? attachmentIds = null)
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
                await Clients.Caller.SendAsync("message_confirmed", new { message_id = message.Id, temp_id = tempId });

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
                        await Clients.Client(connectionId).SendAsync("message_new", new { conversation_id = conversationId, message = messageDto });

                        // Уведомляем отправителя о доставке получателю
                        await Clients.Caller.SendAsync("message_delivered", new
                        {
                            message_id = message.Id,
                            user_id = memberId
                        });

                        // Обновляем счетчик непрочитанных у получателя
                        var newUnreadCount = chat.GetUnreadCount(memberId);
                        await Clients.Client(connectionId).SendAsync("unread_count_updated", new { conversation_id = conversationId, unread_count = newUnreadCount });
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

        public class NewMessageRequest
        {
            public Guid conversation_id { get; set; }
            public string text { get; set; }
            public Guid temp_id { get; set; }
            public long? reply_to_message_id { get; set; }
            public string type { get; set; }
            public List<Guid>? attachment_ids { get; set; }
        }
        public async Task SendMessage(NewMessageRequest msg)
        {
            var ct = Context.ConnectionAborted;
            var userId = Context.GetUserId();
            try
            {
                // 2. Проверки из кэша
                var chat = await _chatCache.GetConversationAsync(msg.conversation_id);

                if (!chat.IsMember(userId))
                    throw new HubException("NOT_MEMBER");

                if (chat.IsWriteRestricted && !chat.IsAdmin(userId))
                    throw new HubException("ONLY_ADMINS_CAN_WRITE");

                // 1. Дедупликация
                var exists = await _db.Messages.AnyAsync(m => m.ConversationId == msg.conversation_id && m.ClientTempId == msg.temp_id);

                if (exists)
                {
                    await Clients.Caller.SendAsync("message_duplicate", new { temp_id = msg.temp_id });
                    return;
                }

                // 3. Валидация параметров
                var messageType = ParseMessageType(msg.type);

                object? replyToObj = null;
                if (msg.reply_to_message_id.HasValue)
                {
                    var replyMessage = await _db.Messages
                        .Include(m => m.Sender)
                        .FirstOrDefaultAsync(m => m.Id == msg.reply_to_message_id
                                                    && m.ConversationId == msg.conversation_id
                                                    && !m.IsDeleted);
                    if (replyMessage == null)
                        throw new HubException("REPLY_TO_MESSAGE_NOT_FOUND");

                    var reply_attachments = await _db.FileAttachments
                        .Where(a => a.MessageId == replyMessage.Id)
                        .Select(a => new AttachmentDto
                        {
                            id = a.Id,
                            file_name = a.FileName,
                            file_size = a.FileSize,
                            mime_type = a.MimeType,
                            url = $"{Program.AppBaseUrl}/api/files/{a.StoragePath}",
                            thumbnail_url = a.ThumbnailPath != null ? $"{Program.AppBaseUrl}/api/files/{a.ThumbnailPath}" : null
                        })
                        .ToListAsync(ct);

                    replyToObj = new
                    {
                        id = replyMessage.Id,
                        sender_id = replyMessage.SenderId,
                        sender_name = replyMessage.Sender.DisplayName,
                        text = replyMessage.Text.Length > 100
                                ? replyMessage.Text[..100] + "..."
                                : replyMessage.Text,
                        type = replyMessage.Type.ToString().ToLower(), 
                        attachments = reply_attachments
                    };
                }

                // 4. Создание сообщения
                var message = new Message
                {
                    ClientTempId = msg.temp_id,
                    ConversationId = msg.conversation_id,
                    SenderId = userId,
                    Text = msg.text ?? string.Empty,
                    Type = messageType,
                    ReplyToMessageId = msg.reply_to_message_id,
                    SentAt = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow,
                    ChannelId = 0
                };

                // 6. Сохранение в БД
                _db.Messages.Add(message);
                await _db.SaveChangesAsync(ct); // message.Id заполняется

                // 5. Привязка вложений
                var attachments = new List<AttachmentDto>();
                if (msg.attachment_ids != null && msg.attachment_ids.Any())
                {
                    var attachmentFiles = await _db.FileAttachments.AsTracking()
                        .Where(a => msg.attachment_ids.Contains(a.Id) && a.UserId == userId)
                        .ToListAsync(ct);

                    foreach (var attachment in attachmentFiles)
                    {
                        attachment.MessageId = message.Id;
                    }

                    attachments = attachmentFiles
                    .Select(f => new AttachmentDto
                        {
                            id = f.Id,
                            file_name = f.FileName,
                            file_size = f.FileSize,
                            mime_type = f.MimeType,
                            url = $"{Program.AppBaseUrl}/api/files/{f.StoragePath}",
                            thumbnail_url = f.ThumbnailPath != null ? $"{Program.AppBaseUrl}/api/files/{f.ThumbnailPath}" : null
                        })
                    .ToList();
                }

                var truncatedText = (msg.text ?? string.Empty).Length > 100 ? msg.text[..100] + "..." : (msg.text ?? string.Empty);

                var conversation = await _db.Conversations.FindAsync(msg.conversation_id);
                conversation.LastMessageId = message.Id;
                conversation.LastMessageText = truncatedText;
                conversation.LastMessageSenderId = userId;
                conversation.LastMessageCreatedAt = message.CreatedAt;
                conversation.UpdatedAt = message.CreatedAt;
                _db.Conversations.Update(conversation);

                var members = await _db.ConversationMembers.AsTracking()
                    .Where(cm => cm.ConversationId == msg.conversation_id && cm.UserId != userId)
                    .ToListAsync();

                foreach (var member in members)
                    member.UnreadCount++;

                await _db.SaveChangesAsync();

                // 7. Обновление кэша
                _chatCache.UpdateLastMessage(msg.conversation_id, message, truncatedText);
                _chatCache.IncrementUnreadCounts(msg.conversation_id, userId);

                // 8. Подтверждение отправителю
                await Clients.Caller.SendAsync("message_confirmed", new { message_id = message.Id, temp_id = msg.temp_id });

                var user = await _db.Users
                    .Where(u => u.Id == userId)
                    .Select(u => new
                    {
                        u.Id,
                        u.DisplayName,
                        u.AvatarUrl,
                        u.CustomStatus
                    })
                    .FirstOrDefaultAsync();

                // 9. Рассылка остальным
                var messageDto = new
                {
                    id = message.Id,
                    type = msg.type,
                    text = message.Text,
                    created_at = message.CreatedAt,
                    sender = new { 
                        id = userId,
                        display_name = user.DisplayName,
                        avatar_url = user.AvatarUrl, 
                        status = "online", 
                        is_online = true,
                        custom_status = user.CustomStatus
                    },
                    reply_to = replyToObj,
                    attachments = attachments,
                };

                await Clients.Group(msg.conversation_id.ToString()).SendAsync("message_new", new
                {
                    conversation_id = msg.conversation_id,
                    message = messageDto
                });

                var lastMessagePreview = new LastMessageDto
                {
                    id = message.Id,
                    text = message.Text.Length > 100 ? message.Text.Substring(0, 100) + "..." : message.Text,
                    type = msg.type,
                    sender_id = userId, 
                    sender_name = user.DisplayName,
                    created_at = message.CreatedAt,
                    attachments = attachments
                };


                string convName = conversation.Name;
                if (conversation.Type == ConversationType.direct)
                {
                    var user2 = await _db.Users
                        .Where(u => u.Id == members.First().UserId)
                        .Select(u => new
                        {
                            u.Id,
                            u.DisplayName,
                            u.AvatarUrl,
                            u.CustomStatus
                        })
                        .FirstOrDefaultAsync();

                    convName = user2.DisplayName;
                }

                var conversationUpdatedDto = new ConversationUpdatedDto()
                {
                    id = msg.conversation_id,
                    type = conversation.Type.ToString().ToLower(),
                    name = convName,
                    avatar_url = conversation.AvatarUrl ?? "",
                    last_message = lastMessagePreview,
                    updated_at = message.CreatedAt
                };
                var onlineMembers = _userCache.GetOnlineMembers(chat.Members).ToList();
                foreach (var memberId in onlineMembers)
                {
                    if (message.Conversation.Type == ConversationType.direct)
                    {
                        if (memberId != userId)
                        {
                            conversationUpdatedDto.name = user.DisplayName;
                            conversationUpdatedDto.avatar_url = user.AvatarUrl;
                        }
                        else
                            (conversationUpdatedDto.name, conversationUpdatedDto.avatar_url) = await GetUserDisplayNameAndAvatar(onlineMembers.FirstOrDefault(u => u != memberId));
                        await Clients.User(memberId.ToString().ToLower()).SendAsync("conversation_updated", conversationUpdatedDto, ct);
                    }
                    else
                    {
                        await Clients.User(memberId.ToString()).SendAsync("conversation_updated", conversationUpdatedDto, ct);
                    }

                    // Обновляем счетчик непрочитанных у получателя
                    if (memberId != userId)
                    {
                        var newUnreadCount = chat.GetUnreadCount(memberId);
                        await Clients.User(memberId.ToString()).SendAsync("unread_count_updated", new { conversation_id = msg.conversation_id, unread_count = newUnreadCount });
                    }
                }

                onlineMembers = onlineMembers.Where(m => m != userId).ToList();
                if (onlineMembers.Count > 0)
                    await Clients.Caller.SendAsync("message_delivered", new
                    {
                        message_id = message.Id,
                        user_ids = onlineMembers  // список Guid
                    });

                _logger.LogDebug("Message {MessageId} sent to conversation {ConversationId} by {UserId}", message.Id, msg.conversation_id, userId);
            }
            catch (HubException)
            {
                throw;
            }
            catch (NotFoundException ex)
            {
                _logger.LogError(ex, "Error sending message to {ConversationId} conversation not found", msg.conversation_id);
                throw new HubException("MESSAGE_SEND_FAILED", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending message to {ConversationId} {message}", msg.conversation_id, ex.Message);
                throw new HubException("MESSAGE_SEND_FAILED", ex);
            }
        }

        async Task<(string, string)> GetUserDisplayNameAndAvatar(Guid userId)
        {
            var user2 = await _db.Users
                .Where(u => u.Id == userId)
                .Select(u => new { u.DisplayName, u.AvatarUrl })
                .FirstOrDefaultAsync();
            return (user2.DisplayName, user2.AvatarUrl);
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


        public class ConversationRequest
        {
            public Guid conversation_id { get; set; }
        }
        public async Task JoinConversation(ConversationRequest data)
        {
            var userId = Context.GetUserId();

            // Проверяем, что пользователь участник чата
            var isMember = await _db.ConversationMembers
                .AnyAsync(cm => cm.ConversationId == data.conversation_id && cm.UserId == userId);

            if (!isMember)
                throw new HubException("NOT_MEMBER");

            await Groups.AddToGroupAsync(Context.ConnectionId, data.conversation_id.ToString());

            // Отправляем последние непрочитанные сообщения (только для этого чата)
            //var member = await _db.ConversationMembers
            //    .FirstOrDefaultAsync(cm => cm.ConversationId == data.conversation_id && cm.UserId == userId);

            //if (member?.UnreadCount > 0)
            //{
            //    var unreadMessages = await _db.Messages
            //        .Where(m => m.ConversationId == data.conversation_id
            //                    && m.Id > (member.LastReadMessageId ?? 0)
            //                    && !m.IsDeleted)
            //        .OrderBy(m => m.Id)
            //        .Take(50)
            //        .Select(m => new MessageDto
            //        {
            //            id = m.Id,
            //            conversation_id = m.ConversationId,
            //            sender_id = m.SenderId,
            //            sender = new UserBriefDto
            //            {
            //                id = m.Sender.Id,
            //                display_name = m.Sender.DisplayName,
            //                avatar_url = m.Sender.AvatarUrl
            //            },
            //            type = m.Type.ToString().ToLower(),
            //            text = m.Text,
            //            created_at = m.CreatedAt
            //        })
            //        .ToListAsync();

            //    await Clients.Caller.SendAsync("unread_messages", new
            //    {
            //        conversation_id = data.conversation_id,
            //        messages = unreadMessages
            //    });

            //    // Сбрасываем счётчик непрочитанных в кэше
            //    _chatCache.ResetUnreadCount(data.conversation_id, userId);
            //}

            _logger.LogDebug("User {UserId} joined conversation {ConversationId}", userId, data.conversation_id);
        }

        public async Task LeaveConversation(ConversationRequest data)
        {
            var userId = Context.GetUserId();
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, data.conversation_id.ToString());
            _logger.LogDebug("User {UserId} left conversation {ConversationId}", userId, data.conversation_id);
        }

        public async Task SendTyping(ConversationRequest data)
        {
            var userId = Context.GetUserId();

            // Проверяем, что пользователь участник чата (быстрая проверка через кэш)
            var chat = await _chatCache.GetConversationAsync(data.conversation_id);
            if (!chat.IsMember(userId))
                return; // тихо игнорируем

            var displayName = _userCache.GetDisplayName(userId) ?? userId.ToString();

            await Clients.Group(data.conversation_id.ToString()).SendAsync("typing_start", new
            {
                conversation_id = data.conversation_id,
                user_id = userId,
                display_name = displayName
            });
        }

        public async Task StopTyping(ConversationRequest data)
        {
            var userId = Context.GetUserId();

            var chat = await _chatCache.GetConversationAsync(data.conversation_id);
            if (!chat.IsMember(userId))
                return;

            await Clients.Group(data.conversation_id.ToString()).SendAsync("typing_stop", new
            {
                conversation_id = data.conversation_id,
                user_id = userId
            });
        }
    }
}
