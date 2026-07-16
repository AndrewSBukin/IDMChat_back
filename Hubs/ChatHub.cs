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
using System.Text.Json;
using System.Text.RegularExpressions;
using static IDMChat.Controllers.FilesController;
using static System.Net.Mime.MediaTypeNames;
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
        private readonly IBackgroundPushQueue _backgroundPushQueue;

        public ChatHub(ChatDbContext dbContext, ChatStateCache chatCache, UserCache userCache, ILogger<ChatHub> logger, IChatPathUrlResolver urlResolver, IBackgroundPushQueue backgroundPushQueue)
        {
            _db = dbContext;
            _chatCache = chatCache;
            _userCache = userCache;
            _logger = logger;
            _urlResolver = urlResolver;
            _backgroundPushQueue = backgroundPushQueue;
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
            _userCache.LeaveConversation(userId);

            // 4. Обновить статус в БД (опционально)
            if (user != null)
            {
                user.IsOnline = false;
                user.LastSeenAt = now;
                await _db.SaveChangesAsync();
            }

            await base.OnDisconnectedAsync(exception);
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

                // ПРОВЕРКА НА КОМАНДУ БОТА
                if (message.Text.StartsWith("/"))
                {
                    // Отправляем задачу в фоновую очередь, чтобы бэк быстро ответил фронту, 
                    // а тяжелый запрос во внешнюю систему ушел в бэкграунд
                    _backgroundPushQueue.Enqueue(new PushNotificationTask
                    {
                        MessageId = message.Id,
                        ConversationId = message.ConversationId,
                        SenderId = userId,
                        MessageText = message.Text,
                        MessageType = "bot_command" // Специальный тип задачи для нашего воркера
                    });
                }
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

        [HubMethodName("PressButton")]
        public async Task<bool> HandleBotButtonClick(Guid conversationId, long messageId, string buttonValue)
        {
            var userId = Context.GetUserId();

            // Бросаем задачу в очередь, чтобы воркер уведомил внешнюю систему о клике
            _backgroundPushQueue.Enqueue(new PushNotificationTask
            {
                MessageId = messageId,
                ConversationId = conversationId,
                SenderId = userId,
                MessageText = buttonValue, // Передаем "Да" или "Нет"
                MessageType = "bot_button_click"
            });

            return true;
        }

        [HubMethodName("ForwardMessages")]
        public async Task<List<MessageDto>> ForwardMessages(HubForwardMessagesDto dto)
        {
            var currentUserId = Context.GetUserId();
            var targetChatId = dto.target_conversation_id;

            // 1. Валидация лимитов и соответствия массивов (Пункты 5.2 и требования фронта)
            if (dto.message_ids == null || dto.message_ids.Count == 0)
                throw new HubException("MESSAGE_IDS_EMPTY");

            if (dto.message_ids.Count > 30)
                throw new HubException("LIMIT_EXCEEDED_MAX_30");

            if (dto.temp_ids == null || dto.temp_ids.Count != dto.message_ids.Count)
                throw new HubException("TEMP_IDS_MISMATCH");

            // 2. Проверка прав на целевой чат через ваш ChatStateCache (Пункт 5.5)
            var targetChat = await _chatCache.GetConversationAsync(targetChatId);
            if (targetChat == null)
                throw new HubException("TARGET_CHAT_NOT_FOUND");

            if (!targetChat.Members.Contains(currentUserId))
                throw new HubException("NOT_A_CHAT_MEMBER");

            if (targetChat.IsWriteRestricted && !targetChat.Admins.Contains(currentUserId))
                throw new HubException("WRITE_RESTRICTED");

            // 3. Загружаем оригинальные сообщения (и проверяем чаты-источники)
            var originalMessages = await _db.Messages
                .Include(m => m.FileAttachments)
                .Include(m => m.Conversation) // Чтобы проверить флаг запрета пересылки в будущем
                .Where(m => dto.message_ids.Contains(m.Id) && !m.IsDeleted)
                .ToListAsync();

            if (!originalMessages.Any())
                throw new HubException("MESSAGES_NOT_FOUND");

            // Сортируем строго в том порядке, в котором их прислал фронт (сохраняем хронологию)
            var sortedOriginals = dto.message_ids
                .Select(id => originalMessages.FirstOrDefault(m => m.Id == id))
                .Where(m => m != null)
                .ToList();

            var newMessages = new List<Message>();
            var newAttachments = new List<FileAttachment>();

            // Карта для связи оригинального Message.Id -> temp_id от фронта
            var idToTempMap = dto.message_ids
                .Zip(dto.temp_ids, (id, tempId) => new { id, tempId })
                .ToDictionary(x => x.id, x => x.tempId);

            // Список для хранения созданных DTO, которые вернем в ответе метода
            var resultDtos = new List<MessageDto>();

            foreach (var orig in sortedOriginals)
            {
                // Валидация: Запрет пересылки системных сообщений (Пункт 5.4)
                if (orig.Type == MessageType.System)
                    throw new HubException("SYSTEM_MESSAGES_CANNOT_BE_FORWARDED");

                // Валидация на будущее: Запрет пересылки из приватных каналов (Пункт 5.1)
                // if (orig.Conversation.ForwardingDisabled) throw new HubException("FORWARDING_DISABLED_BY_SOURCE");

                // Транзитивность (Пункт 4)
                Guid originalSenderId = orig.IsForwarded && orig.OriginalSenderId.HasValue
                    ? orig.OriginalSenderId.Value
                    : orig.SenderId;

                var newMessage = new Message
                {
                    SenderId = currentUserId,
                    ConversationId = targetChatId,
                    Text = orig.Text,
                    Type = orig.Type,
                    CreatedAt = DateTime.UtcNow,
                    SentAt = DateTime.UtcNow,
                    IsForwarded = true,
                    OriginalSenderId = originalSenderId
                    // Реакции, reply_to и read_count по умолчанию создаются пустыми/нулевыми (Пункт 5.3)
                };

                newMessages.Add(newMessage);

                // Копируем вложения в БД (Пункт 3 - Вариант B)
                if (orig.FileAttachments != null)
                {
                    foreach (var origAtt in orig.FileAttachments)
                    {
                        newAttachments.Add(new FileAttachment
                        {
                            Id = Guid.NewGuid(),
                            Message = newMessage,
                            ConversationId = targetChatId,
                            UserId = currentUserId,
                            FileName = origAtt.FileName,
                            FileSize = origAtt.FileSize,
                            MimeType = origAtt.MimeType,
                            StoragePath = origAtt.StoragePath, // Тот же файл на диске
                            ThumbnailPath = origAtt.ThumbnailPath,
                            Duration = origAtt.Duration,
                            Type = origAtt.Type,
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                }
            }

            // Сохраняем все в БД
            _db.Messages.AddRange(newMessages);
            if (newAttachments.Any()) _db.FileAttachments.AddRange(newAttachments);
            await _db.SaveChangesAsync();

            // 4. Обновляем денормализацию чата (БД + Кэш памяти)
            var lastMessage = newMessages.Last();
            string truncatedText = lastMessage.Text?.Length > 100 ? lastMessage.Text.Substring(0, 100) + "..." : lastMessage.Text ?? string.Empty;

            await _db.Conversations
                .Where(c => c.Id == targetChatId)
                .ExecuteUpdateAsync(calls => calls
                    .SetProperty(c => c.LastMessageId, lastMessage.Id)
                    .SetProperty(c => c.LastMessageText, truncatedText)
                    .SetProperty(c => c.LastMessageSenderId, lastMessage.SenderId)
                    .SetProperty(c => c.LastMessageCreatedAt, lastMessage.CreatedAt)
                    .SetProperty(c => c.UpdatedAt, DateTime.UtcNow)
                );

            _chatCache.UpdateLastMessage(targetChatId, lastMessage, truncatedText);

            // 5. Маппинг в MessageDto и рассылка событий
            var senderFromCache = _userCache.GetUser(currentUserId);

            foreach (var message in newMessages)
            {
                var originalSenderFromCache = _userCache.GetUser(message.OriginalSenderId!.Value);

                // Находим оригинальный ID сообщения, чтобы вытащить привязанный к нему temp_id
                // (Так как порядок сохранился, мы можем сопоставить их по цепочке)
                long origId = sortedOriginals[newMessages.IndexOf(message)].Id;
                string currentTempId = idToTempMap[origId];

                var attachmentsDto = message.FileAttachments?.Select(att => new AttachmentDto
                {
                    id = att.Id,
                    file_name = att.FileName,
                    file_size = att.FileSize,
                    mime_type = att.MimeType,
                    url = _urlResolver.ResolveUrl(att.StoragePath),
                    thumbnail_url = _urlResolver.ResolveUrl(att.ThumbnailPath),
                    duration = att.Duration,
                    type = att.Type, 
                    waveform = !string.IsNullOrEmpty(att.WaveformJson)
                        ? JsonSerializer.Deserialize<List<double>>(att.WaveformJson)
                        : null
                }).ToList();

                // Формируем DTO
                var messagedto = new MessageDto
                {
                    id = message.Id,
                    conversation_id = targetChatId,
                    sender_id = currentUserId,
                    type = message.Type.ToString().ToLower(),
                    text = message.Text,
                    created_at = message.CreatedAt,
                    attachments = attachmentsDto ?? new List<AttachmentDto>(),

                    reply_to = null,
                    reply_to_id = null,
                    mentions = new List<UserMention>(),
                    read_count = 0,
                    read_by = new List<UserBriefDto>(),
                    is_edited = false,
                    is_deleted = false,
                    updated_at = null,

                    sender = new UserBriefDto
                    {
                        id = currentUserId,
                        display_name = senderFromCache?.DisplayName ?? "-",
                        avatar_url = _urlResolver.ResolveUrl(senderFromCache?.AvatarUrl)
                    },

                    is_forwarded = true,
                    forward_from = new UserBriefDto
                    {
                        id = message.OriginalSenderId.Value,
                        display_name = originalSenderFromCache?.DisplayName ?? "Удаленный пользователь",
                        avatar_url = _urlResolver.ResolveUrl(originalSenderFromCache?.AvatarUrl)
                    }
                };

                resultDtos.Add(messagedto);

                // Рассылаем обычное событие message_new в группу целевого чата (Требование фронта)
                // Для инициатора прокидываем анонимный объект с temp_id, чтобы он мог сделать match
                await Clients.Group(targetChatId.ToString()).SendAsync("message_new", new
                {
                    conversation_id = targetChatId,
                    message = messagedto
                });

                // 2. Отправляем персональное подтверждение ТОЛЬКО автору запроса (Clients.Caller)
                // Полностью повторяем ваш существующий механизм для бесшовной интеграции!
                await Clients.Caller.SendAsync("message_confirmed", new
                {
                    message_id = message.Id,
                    temp_id = currentTempId
                });
            }

            // Push message
            var allMembers = targetChat.Members;
            var pushRecipients = allMembers.Where(userId => userId != currentUserId).ToList();
            if (pushRecipients.Any())
            {
                var lastForwardedMessage = newMessages.Last();

                string pushText = newMessages.Count == 1
                    ? (lastForwardedMessage.Type == MessageType.Text
                        ? $"Пересланное сообщение: {lastForwardedMessage.Text}"
                        : $"Переслал(а) вложение [{lastForwardedMessage.Type}]")
                    : $"Переслал(а) {newMessages.Count} сообщений";

                string finalMessageType;
                if (newMessages.Count == 1)
                {
                    finalMessageType = lastForwardedMessage.Type.ToString().ToLower();
                }
                else
                {
                    // Проверяем, одинаковый ли тип у ВСЕХ пересылаемых сообщений в пачке
                    var firstType = newMessages.First().Type;
                    bool allSameType = newMessages.All(m => m.Type == firstType);

                    // Если все сообщения одного типа (например, все картинки) — сохраняем этот тип.
                    // Если типы разные (микс из текста и файлов) — ставим "text", так как пуш будет просто строкой текста.
                    finalMessageType = allSameType ? firstType.ToString().ToLower() : "text";
                }

                _backgroundPushQueue.Enqueue(new PushNotificationTask
                {
                    ConversationId = targetChatId,
                    SenderId = currentUserId,
                    MessageText = pushText, // Передаем адаптированный под форвард текст
                    MessageType = finalMessageType,
                    MessageId = lastForwardedMessage.Id,

                    // Передаем отфильтрованный список участников целевого чата
                    TargetUserIds = pushRecipients
                });
            }

            // Возвращаем список созданных сообщений в ответе hub-метода
            return resultDtos;
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

            _userCache.JoinConversation(userId, data.conversation_id);

            _logger.LogDebug("User {UserId} joined conversation {ConversationId}", userId, data.conversation_id);
        }

        public async Task LeaveConversation(ConversationRequest data)
        {
            var userId = Context.GetUserId();
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, data.conversation_id.ToString());
            _userCache.LeaveConversation(userId);
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
