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
        private readonly INewMessageService _newMessageService;

        public ChatHub(ChatDbContext dbContext, ChatStateCache chatCache, UserCache userCache, ILogger<ChatHub> logger, IChatPathUrlResolver urlResolver, IBackgroundPushQueue backgroundPushQueue, INewMessageService newMessageService)
        {
            _db = dbContext;
            _chatCache = chatCache;
            _userCache = userCache;
            _logger = logger;
            _urlResolver = urlResolver;
            _backgroundPushQueue = backgroundPushQueue;
            _newMessageService = newMessageService;
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

            var message = await _newMessageService.HandleSendMessage(msg, userId, ct);

            // ПРОВЕРКА НА КОМАНДУ БОТА
            if (message != null && message.Text.StartsWith("/"))
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

        /// <summary>
        /// По большей части совпадает с простой отправкой массива сообщений _newMessageService.HandleSendMessage, но с нюансами по сложениям, автору, упоминаниям и реакциям
        /// Возможно потом объединим, но пока форвард только из хаба - оставляем так.
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        /// <exception cref="HubException"></exception>
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
