using Azure.Core;
using IDMChat.Controllers;
using IDMChat.Domain;
using IDMChat.DTO;
using IDMChat.Models;
using IDMChat.Services;
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
        private readonly IChatPathUrlResolver _urlResolver;

        public ChatHub(ChatDbContext dbContext, ChatStateCache chatCache, UserCache userCache, ILogger<ChatHub> logger, IChatPathUrlResolver urlResolver)
        {
            _db = dbContext;
            _chatCache = chatCache;
            _userCache = userCache;
            _logger = logger;
            _urlResolver = urlResolver;
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

        public class MentionItem
        {
            public Guid user_id { get; set; }
            public string display_name { get; set; }
        }
        public class NewMessageRequest
        {
            public Guid conversation_id { get; set; }
            public string text { get; set; }
            public Guid temp_id { get; set; }
            public long? reply_to_message_id { get; set; }
            public string type { get; set; }
            public List<Guid>? attachment_ids { get; set; }
            public List<MentionItem> mentions { get; set; }
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
                                                    && !m.IsDeleted, ct);
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
                            url = _urlResolver.ResolveUrl(a.StoragePath),
                            thumbnail_url = _urlResolver.ResolveUrl(a.ThumbnailPath)
                        })
                        .ToListAsync(ct);

                    replyToObj = new
                    {
                        id = replyMessage.Id,
                        sender_id = replyMessage.SenderId,
                        sender_name = replyMessage.Sender.DisplayName ?? "-",
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

                var linkRegex = new System.Text.RegularExpressions.Regex(
                    @"https?://[^\s]+",
                    System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                var linkMatches = linkRegex.Matches(message.Text);

                // 2. Если ссылки найдены — пишем их в индексную таблицу
                if (linkMatches.Count > 0)
                {
                    // Используем Distinct, чтобы если юзер прислал две одинаковые ссылки в одном сообщении, база не ругалась на PK
                    var uniqueUrls = linkMatches.Cast<System.Text.RegularExpressions.Match>()
                        .Select(m => m.Value)
                        .Distinct()
                        .ToList();

                    foreach (var url in uniqueUrls)
                    {
                        _db.MessageLinks.Add(new MessageLink
                        {
                            MessageId = message.Id,
                            ConversationId = message.ConversationId, // Берем из входящего запроса
                            Url = url,
                            CreatedAt = message.CreatedAt
                        });
                    }

                    // Сохраняем пачкой. EF Core объединит эти инсерты в один легкий батч
                    await _db.SaveChangesAsync(ct);
                }

                // 5. Обработка упоминаний (Mentions) — НАША НОВАЯ ФИЧА
                var mentionsDto = new List<UserMention>();
                if (msg.mentions != null && msg.mentions.Any())
                {
                    // Тегнуть можно только тех, кто состоит в этом чате (Валидация по кэшу чата в памяти)
                    var validMentions = msg.mentions
                        .Where(m => chat.Members.Contains(m.user_id))
                        .Distinct()
                        .ToList();

                    if (validMentions.Any())
                    {
                        foreach (var m in validMentions)
                        {
                            // Пишем связь в новую промежуточную таблицу (Запросы накопятся в контексте)
                            _db.MessageMentions.Add(new MessageMention { MessageId = message.Id, UserId = m.user_id, DisplayName = m.display_name  });

                            mentionsDto.Add(new UserMention(m.user_id, m.display_name));
                        }
                        await _db.SaveChangesAsync(ct);
                    }
                }

                // 6. Привязка вложений
                var attachments = new List<AttachmentDto>();
                if (msg.attachment_ids != null && msg.attachment_ids.Any())
                {
                    var attachmentFiles = await _db.FileAttachments.AsTracking()
                        .Where(a => msg.attachment_ids.Contains(a.Id) && a.UserId == userId)
                        .ToListAsync(ct);

                    foreach (var attachment in attachmentFiles)
                    {
                        attachment.MessageId = message.Id;
                        attachment.ConversationId = message.ConversationId;
                    }

                    attachments = attachmentFiles
                    .Select(f => new AttachmentDto
                        {
                            id = f.Id,
                            file_name = f.FileName,
                            file_size = f.FileSize,
                            mime_type = f.MimeType,
                            url = _urlResolver.ResolveUrl(f.StoragePath),
                            thumbnail_url = _urlResolver.ResolveUrl(f.ThumbnailPath)
                        })
                    .ToList();
                    await _db.SaveChangesAsync(ct);
                }

                var truncatedText = (msg.text ?? string.Empty).Length > 100 ? msg.text[..100] + "..." : (msg.text ?? string.Empty);

                await _db.Conversations
                    .Where(c => c.Id == msg.conversation_id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(c => c.LastMessageId, message.Id)
                        .SetProperty(c => c.LastMessageText, truncatedText)
                        .SetProperty(c => c.LastMessageSenderId, userId)
                        .SetProperty(c => c.LastMessageCreatedAt, message.CreatedAt)
                        .SetProperty(c => c.UpdatedAt, message.CreatedAt), ct);

                await _db.ConversationMembers
                    .Where(cm => cm.ConversationId == msg.conversation_id && cm.UserId != userId)
                    .ExecuteUpdateAsync(s => s.SetProperty(cm => cm.UnreadCount, cm => cm.UnreadCount + 1), ct);

                // 7. Обновление кэша
                _chatCache.UpdateLastMessage(msg.conversation_id, message, truncatedText);
                _chatCache.IncrementUnreadCounts(msg.conversation_id, userId);

                // 8. Подтверждение отправителю
                await Clients.Caller.SendAsync("message_confirmed", new { message_id = message.Id, temp_id = msg.temp_id });

                var sender = _userCache.GetUser(userId);

                // 9. Рассылка остальным
                var messageDto = new
                {
                    id = message.Id,
                    type = msg.type,
                    text = message.Text,
                    created_at = message.CreatedAt,
                    sender = new { 
                        id = userId,
                        display_name = sender?.DisplayName ?? "-",
                        avatar_url = _urlResolver.ResolveUrl(sender?.AvatarUrl), 
                        status = "online", 
                        is_online = true,
                        custom_status = sender?.CustomStatus, 
                        last_seen_at = sender?.LastSeenAt
                    },
                    reply_to = replyToObj,
                    attachments = attachments, 
                    mentions = mentionsDto
                };

                await Clients.Group(msg.conversation_id.ToString()).SendAsync("message_new", new
                {
                    conversation_id = msg.conversation_id,
                    message = messageDto
                }, ct);

                var lastMessagePreview = new LastMessageDto
                {
                    id = message.Id,
                    text = truncatedText,
                    type = msg.type,
                    sender_id = userId, 
                    sender_name = sender.DisplayName ?? "-",
                    created_at = message.CreatedAt,
                    attachments = attachments, 
                    mentions = mentionsDto
                };

                var onlineMembers = _userCache.GetOnlineMembers(chat.Members).ToList();

                if (chat.Type == ConversationType.direct)
                {
                    // Для директ-чата имя собеседника определяем полностью в памяти
                    var otherMemberId = chat.Members.FirstOrDefault(id => id != userId);
                    var otherMember = _userCache.GetUser(otherMemberId);
                    var otherMemberName = otherMember?.DisplayName ?? "-";
                    var otherMemberAvatar = _urlResolver.ResolveUrl(otherMember?.AvatarUrl);

                    // Конструктор для отправителя (он видит имя получателя)
                    var updateForSender = new ConversationUpdatedDto
                    {
                        id = msg.conversation_id,
                        type = "direct",
                        name = otherMemberName,
                        avatar_url = otherMemberAvatar,
                        last_message = lastMessagePreview,
                        updated_at = message.CreatedAt
                    };
                    await Clients.Caller.SendAsync("conversation_updated", updateForSender, ct);

                    // Рассылаем точечно (так как в директе всего 2 человека, это не создаст нагрузки)
                    if (onlineMembers.Contains(otherMemberId))
                    {
                        // Конструктор для получателя (он видит имя отправителя)
                        var updateForRecipient = new ConversationUpdatedDto
                        {
                            id = msg.conversation_id,
                            type = "direct",
                            name = sender.DisplayName ?? "-",
                            avatar_url = _urlResolver.ResolveUrl(sender.AvatarUrl),
                            last_message = lastMessagePreview,
                            updated_at = message.CreatedAt
                        };

                        await Clients.User(otherMemberId.ToString().ToLower()).SendAsync("conversation_updated", updateForRecipient, ct);
                    }
                }
                else
                {
                    // Для групповых чатов объект обновления для всех ОДИНАКОВЫЙ.
                    var groupUpdateDto = new ConversationUpdatedDto { 
                        id = msg.conversation_id, 
                        type = chat.Type.ToString().ToLower(), 
                        name = chat.Name, 
                        avatar_url = _urlResolver.ResolveUrl(chat.AvatarUrl) ?? "", 
                        last_message = lastMessagePreview, 
                        updated_at = message.CreatedAt 
                    };

                    var onlineUserStrings = onlineMembers.Select(id => id.ToString()).ToList(); 
                    await Clients.Users(onlineUserStrings).SendAsync("conversation_updated", groupUpdateDto, ct);
                }

                onlineMembers = onlineMembers.Where(m => m != userId).ToList();
                // Обновляем счетчик непрочитанных у получателя
                foreach (var memberId in onlineMembers)
                {
                    var newUnreadCount = chat.GetUnreadCount(memberId);
                    await Clients.User(memberId.ToString()).SendAsync("unread_count_updated", new { conversation_id = msg.conversation_id, unread_count = newUnreadCount }, ct);
                }

                if (onlineMembers.Any())
                    await Clients.Caller.SendAsync("message_delivered", new
                    {
                        message_id = message.Id,
                        user_ids = onlineMembers  // список Guid
                    });

                _logger.LogDebug("Message {MessageId} sent to conversation {ConversationId} by {UserId}", message.Id, msg.conversation_id, userId);

                // PUSH
                // Вытаскиваем ID тех, кого реально упомянули (мы собирали их в блоке Mentions)
                var validMentionIds = msg.mentions != null
                    ? msg.mentions.Where(m => chat.Members.Contains(m.user_id)).Select(m => m.user_id).ToList()
                    : new List<Guid>();

                // Скидываем тяжелую задачу отправки пушей в фоновую очередь, полностью освобождая основной поток чата
                //_backgroundQueue.QueueBackgroundWorkItem(async token =>
                //{
                //    using var scope = _serviceProvider.CreateScope();
                //    var pushService = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();

                //    // Передаем сущность сообщения, список меншенов и сырой тип ("text"/"image"...)
                //    await pushService.SendNewMessagePushAsync(message, validMentionIds, msg.type);
                //});
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

            var displayName = _userCache.GetUser(userId).DisplayName ?? userId.ToString();

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
