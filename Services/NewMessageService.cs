using IDMChat.Domain;
using IDMChat.DTO;
using IDMChat.Hubs;
using IDMChat.Models;
using IDMChat.Utils;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using static IDMChat.Controllers.FilesController;
using static IDMChat.Hubs.ChatHub;

namespace IDMChat.Services
{
    public interface INewMessageService
    {
        Task<Message?> HandleSendMessage(NewMessageRequest msg, Guid userId, CancellationToken ct = default);
        Task<Message?> HandleUpdateMessage(NewMessageRequest msg, Guid userId, CancellationToken ct = default);
        Task<Message?> HandleSendSystemMessage(Guid conversationId, string text, CancellationToken ct = default);
    }


    public class NewMessageService: INewMessageService
    {
        private readonly ChatDbContext _db;
        private readonly ChatStateCache _chatCache;
        private readonly UserCache _userCache;
        private readonly ILogger<ChatHub> _logger;
        private readonly IChatPathUrlResolver _urlResolver;
        private readonly IBackgroundPushQueue _backgroundPushQueue;
        private readonly IHubContext<ChatHub> _hubContext;

        public NewMessageService(ChatDbContext dbContext, ChatStateCache chatCache, UserCache userCache, ILogger<ChatHub> logger, IChatPathUrlResolver urlResolver, IBackgroundPushQueue backgroundPushQueue, IHubContext<ChatHub> hubContext)
        {
            _db = dbContext;
            _chatCache = chatCache;
            _userCache = userCache;
            _logger = logger;
            _urlResolver = urlResolver;
            _backgroundPushQueue = backgroundPushQueue;
            _hubContext = hubContext;
        }

        public async Task<Message?> HandleSendSystemMessage(Guid conversationId, string text, CancellationToken ct = default)
        {
            var systemMessage = new ChatHub.NewMessageRequest
            {
                conversation_id = conversationId,
                text = text,
                type = "system",
                attachment_ids = new List<Guid>(),
                mentions = new List<ChatHub.MentionItem>(),
                reply_to_message_id = null,
                temp_id = Guid.NewGuid()
            };
            return await HandleSendMessage(systemMessage, Guid.Empty, ct);
        }

        private bool isBotOrSystem(Guid id)
        {
            return id == Guid.Empty || id == Guid.Parse("00000000-0000-0000-0000-000000000001");
        }
        public async Task<Message?> HandleSendMessage(NewMessageRequest msg, Guid userId, CancellationToken ct = default)
        {
            try
            {
                // 2. Проверки из кэша
                var chat = await _chatCache.GetConversationAsync(msg.conversation_id);

                if (!isBotOrSystem(userId) && !chat.IsMember(userId))
                    throw new HubException("NOT_MEMBER");

                if (!isBotOrSystem(userId) && chat.IsWriteRestricted && !chat.IsAdmin(userId))
                    throw new HubException("ONLY_ADMINS_CAN_WRITE");

                // 1. Дедупликация
                var exists = await _db.Messages.AnyAsync(m => m.ConversationId == msg.conversation_id && m.ClientTempId == msg.temp_id);
                if (exists)
                {
                    if (!isBotOrSystem(userId))
                        await _hubContext.Clients.User(userId.ToString()).SendAsync("message_duplicate", new { temp_id = msg.temp_id });
                    return null;
                }

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

                // 3. Валидация параметров
                //var messageType = ParseMessageType(msg.type);

                var calculatedMessageType = MessageType.Text;
                if (msg.attachment_ids != null && msg.attachment_ids.Any())
                {
                    var attachedFileTypes = await _db.FileAttachments
                        .Where(f => msg.attachment_ids.Contains(f.Id))
                        .Select(f => f.Type)
                        .ToListAsync(ct);

                    if (attachedFileTypes.Any())
                    {
                        var firstAttachmentType = attachedFileTypes.First();
                        bool isHomogeneous = attachedFileTypes.All(a => a == firstAttachmentType);
                        if (isHomogeneous)
                        {
                            calculatedMessageType = firstAttachmentType switch
                            {
                                FileType.Image => MessageType.Image,
                                FileType.Video => MessageType.Video,
                                FileType.Voice => MessageType.Voice,
                                _ => MessageType.File // Для всех остальных документов
                            };
                        }
                        else
                            calculatedMessageType = MessageType.Mixed;
                    }
                }
                if (msg.type == "system")
                    calculatedMessageType = MessageType.System;

                // 4. Создание сообщения
                var message = new Message
                {
                    ClientTempId = msg.temp_id,
                    ConversationId = msg.conversation_id,
                    SenderId = userId,
                    Text = msg.text ?? string.Empty,
                    Type = calculatedMessageType,
                    ReplyToMessageId = msg.reply_to_message_id,
                    SentAt = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow,
                    ChannelId = 0, 
                    ExternalIdmId = msg.ext_id
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

                // 5. Обработка упоминаний (Mentions)
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
                            _db.MessageMentions.Add(new MessageMention { MessageId = message.Id, UserId = m.user_id, DisplayName = m.display_name });

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
                        thumbnail_url = _urlResolver.ResolveUrl(f.ThumbnailPath),
                        duration = f.Duration,
                        type = f.Type,
                        waveform = !string.IsNullOrEmpty(f.WaveformJson)
                            ? JsonSerializer.Deserialize<List<double>>(f.WaveformJson)
                            : null
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
                if (!isBotOrSystem(userId))
                    await _hubContext.Clients.User(userId.ToString()).SendAsync("message_confirmed", new { message_id = message.Id, temp_id = msg.temp_id });

                var sender = _userCache.GetUser(userId);

                // 9. Рассылка остальным
                var messageDto = new
                {
                    id = message.Id,
                    type = msg.type,
                    text = message.Text,
                    created_at = message.CreatedAt,
                    sender = new
                    {
                        id = userId,
                        display_name = sender?.DisplayName ?? "",
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

                await _hubContext.Clients.Group(msg.conversation_id.ToString()).SendAsync("message_new", new
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
                    sender_name = sender?.DisplayName ?? "",
                    created_at = message.CreatedAt,
                    attachments = attachments,
                    mentions = mentionsDto
                };

                var onlineMembers = _userCache.GetOnlineMembers(chat.Members).ToList();

                if (chat.Type == ConversationType.direct)
                {
                    // Для директ-чата имя собеседника определяем полностью в памяти
                    var otherMemberId = chat.Members.FirstOrDefault(id => id != userId);
                    if (!isBotOrSystem(userId))
                    {
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
                        await _hubContext.Clients.User(userId.ToString()).SendAsync("conversation_updated", updateForSender, ct);
                    }

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

                        await _hubContext.Clients.User(otherMemberId.ToString().ToLower()).SendAsync("conversation_updated", updateForRecipient, ct);
                    }
                }
                else
                {
                    // Для групповых чатов объект обновления для всех ОДИНАКОВЫЙ.
                    var groupUpdateDto = new ConversationUpdatedDto
                    {
                        id = msg.conversation_id,
                        type = chat.Type.ToString().ToLower(),
                        name = chat.Name,
                        avatar_url = _urlResolver.ResolveUrl(chat.AvatarUrl) ?? "",
                        last_message = lastMessagePreview,
                        updated_at = message.CreatedAt
                    };

                    var onlineUserStrings = onlineMembers.Select(id => id.ToString()).ToList();
                    await _hubContext.Clients.Users(onlineUserStrings).SendAsync("conversation_updated", groupUpdateDto, ct);
                }

                onlineMembers = onlineMembers.Where(m => m != userId).ToList();

                // Обновляем счетчик непрочитанных у получателя
                foreach (var memberId in onlineMembers)
                {
                    var newUnreadCount = chat.GetUnreadCount(memberId);
                    var lastReadMsgId = chat.GetLastReadMessageId(memberId);
                    await _hubContext.Clients.User(memberId.ToString()).SendAsync("unread_count_updated", new UnreadCountUpdatedPayload { conversation_id = msg.conversation_id, unread_count = newUnreadCount, last_read_message_id = lastReadMsgId }, ct);
                    _logger.LogDebug($"my-debug unread_count_updated sent to conversation {msg.conversation_id} for user {memberId.ToString()} newUnreadCount: {newUnreadCount}");
                }

                if (!isBotOrSystem(userId) && onlineMembers.Any())
                    await _hubContext.Clients.User(userId.ToString()).SendAsync("message_delivered", new
                    {
                        message_id = message.Id,
                        user_ids = onlineMembers  // список Guid
                    });

                _logger.LogDebug("my-debug Message {MessageId} sent to conversation {ConversationId} by {UserId}", message.Id, msg.conversation_id, userId);

                // PUSH
                // Вытаскиваем ID тех, кого реально упомянули (мы собирали их в блоке Mentions)
                var validMentionIds = msg.mentions != null
                    ? msg.mentions.Where(m => chat.Members.Contains(m.user_id)).Select(m => m.user_id).ToList()
                    : new List<Guid>();

                // Скидываем тяжелую задачу отправки пушей в фоновую очередь, полностью освобождая основной поток чата
                _backgroundPushQueue.Enqueue(new PushNotificationTask
                {
                    // Заполняем DTO данными, которые батч-процессор отправит на шлюз
                    ConversationId = message.ConversationId,
                    SenderId = message.SenderId,
                    MessageText = message.Text,
                    MessageType = msg.type,
                    MessageId = message.Id,

                    // Передаем ID пользователей, кому предназначен пуш (например, меншены или все участники чата)
                    TargetUserIds = validMentionIds.ToList()
                });

                return message;
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

        public async Task<Message?> HandleUpdateMessage(NewMessageRequest msg, Guid userId, CancellationToken ct = default)
        {
            return null;
        }
    }
}
