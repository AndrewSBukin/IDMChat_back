using FirebaseAdmin.Messaging;
using IDMChat.DTO;
using IDMChat.Hubs;
using IDMChat.Models;
using IDMChat.Utils;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Message = FirebaseAdmin.Messaging.Message;

namespace IDMChat.Services
{
    public class PushBatchProcessor : BackgroundService
    {
        private readonly IBackgroundPushQueue _queue;
        private readonly ILogger<PushBatchProcessor> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly ChatStateCache _chatCache;
        private readonly UserCache _userCache;
        private readonly List<PushNotificationTask> _batch;
        private readonly TimeSpan _flushInterval;
        private readonly int _batchSize;
        private readonly IHubContext<ChatHub> _hubContext;

        public PushBatchProcessor(IBackgroundPushQueue queue, ILogger<PushBatchProcessor> logger, IConfiguration configuration, IServiceProvider serviceProvider, ChatStateCache chatCache, UserCache userCache, IHubContext<ChatHub> hubContext)
        {
            try
            {
                _logger = logger;
                _queue = queue;
                _serviceProvider = serviceProvider;
                _chatCache = chatCache;
                _userCache = userCache;
                _hubContext = hubContext;

                logger.LogInformation("PushBatchProcessor constructor started");

                _flushInterval = TimeSpan.FromSeconds(configuration.GetValue("PushNotifications:BatchFlushIntervalSeconds", 2));
                int configuredBatchSize = configuration.GetValue("PushNotifications:BatchSize", 100);
                _batchSize = Math.Min(configuredBatchSize, 500);
                _batch = new List<PushNotificationTask>(_batchSize);

                _logger.LogInformation("PushBatchProcessor constructor completed. FlushInterval: {FlushInterval}, BatchSize: {BatchSize}", _flushInterval, _batchSize);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "PushBatchProcessor constructor failed");
                throw;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("PushBatchProcessor started. Flush interval: {FlushInterval}s, Batch size: {BatchSize}",
                _flushInterval.TotalSeconds, _batchSize);

            var enumerator = _queue.DequeueAllAsync(stoppingToken).GetAsyncEnumerator();

            try
            {
                var moveNextTask = enumerator.MoveNextAsync().AsTask();
                var lastFlushTime = DateTime.UtcNow;

                while (!stoppingToken.IsCancellationRequested)
                {
                    var timeUntilFlush = _flushInterval - (DateTime.UtcNow - lastFlushTime);
                    var timerDelay = timeUntilFlush > TimeSpan.Zero ? timeUntilFlush : TimeSpan.Zero;
                    var timerTask = Task.Delay(timerDelay, stoppingToken);

                    var completed = await Task.WhenAny(moveNextTask, timerTask);

                    if (completed == timerTask && !stoppingToken.IsCancellationRequested)
                    {
                        await timerTask; // Unwrap
                        if (_batch.Count > 0)
                        {
                            await FlushBatchAsync(stoppingToken);
                            lastFlushTime = DateTime.UtcNow;
                        }
                        continue; // Don't process logs this iteration
                    }

                    if (completed == moveNextTask)
                    {
                        if (!await moveNextTask)
                        {
                            _logger.LogInformation("Push queue completed, shutting down");
                            break;
                        }
                        _batch.Add(enumerator.Current);

                        if (_batch.Count >= _batchSize)
                        {
                            await FlushBatchAsync(stoppingToken);
                            lastFlushTime = DateTime.UtcNow;
                        }
                        moveNextTask = enumerator.MoveNextAsync().AsTask();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("PushBatchProcessor stopping");
            }
            finally
            {
                try
                {
                    await enumerator.DisposeAsync();
                }
                catch (NotSupportedException)
                {
                    // Известная проблема - игнорируем
                    _logger.LogDebug("DisposeAsync not supported - ignoring");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing enumerator");
                }
                await FlushBatchAsync(CancellationToken.None);
            }
        }

        private async Task FlushBatchAsync(CancellationToken ct)
        {
            if (_batch.Count == 0) return;

            var batchToSend = _batch.ToList();
            _batch.Clear();
            _logger.LogDebug($"my-debug FlushBatchAsync batchToSend: {batchToSend.Count}");
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

                // Собираем из батча все задачи, где были реальные упоминания пользователей
                var mentionTasks = batchToSend
                    .Where(t => t.TargetUserIds != null && t.TargetUserIds.Any())
                    .ToList();
                _logger.LogDebug($"my-debug mentionTasks: {mentionTasks.Count}");
                if (mentionTasks.Any())
                {
                    // Группируем по парам ChatId + UserId, чтобы не делать дублирующие запросы
                    var uniqueUserConversations = mentionTasks
                        .SelectMany(t => t.TargetUserIds.Select(userId => new { t.ConversationId, UserId = userId }))
                        .Distinct()
                        .GroupBy(uc => uc.ConversationId);

                    foreach (var conversationGroup in uniqueUserConversations)
                    {
                        var conversationId = conversationGroup.Key;
                        var userIdsInChat = conversationGroup.Select(g => g.UserId).ToList();

                        // За один запрос пачкой собираем UnreadCount и списки непрочитанных ID меншенов
                        var membersData = await db.ConversationMembers
                            .Where(cm => cm.ConversationId == conversationId && userIdsInChat.Contains(cm.UserId))
                            .Select(cm => new
                            {
                                cm.UserId,
                                cm.UnreadCount,
                                cm.LastReadMessageId,
                                UnreadMentionIds = db.Messages
                                    .Where(m => m.ConversationId == conversationId
                                             && !m.IsDeleted
                                             && (cm.LastReadMessageId == null || m.Id > cm.LastReadMessageId)
                                             && m.Mentions.Any(mention => mention.UserId == cm.UserId))
                                    .OrderBy(m => m.Id)
                                    .Select(m => m.Id.ToString())
                            .ToList()
                            })
                        .ToListAsync(ct);

                        _logger.LogDebug($"my-debug membersData: {membersData.Count}");
                        // Рассылаем SignalR ивенты персонально каждому упомянутому сотруднику
                        foreach (var memberInfo in membersData)
                        {
                            var connectionId = _userCache.GetConnectionId(memberInfo.UserId);

                            if (!string.IsNullOrEmpty(connectionId))
                            {
                                
                                //// 1. unread_count_updated (без упоминаний)
                                //_ = _hubContext.Clients.Client(connectionId).SendAsync("unread_count_updated", new
                                //{
                                //    conversation_id = conversationId,
                                //    unread_count = memberInfo.UnreadCount,
                                //    last_read_message_id = memberInfo.LastReadMessageId?.ToString()
                                //}, ct);

                                // 2. отправляем только список ID упоминаний
                                var mentionsPayload = new UnreadMentionsUpdatedPayload
                                {
                                    conversation_id = conversationId,
                                    unread_mention_ids = memberInfo.UnreadMentionIds
                                };
                                _ = _hubContext.Clients.Client(connectionId).SendAsync("unread_mentions_updated", mentionsPayload, ct);
                                _logger.LogDebug($"my-debug unread_mentions_updated sent to conversation {conversationId} for user {memberInfo.UserId.ToString()} unread_mention_ids: {string.Join(',',mentionsPayload.unread_mention_ids)}");
                            }
                        }
                    }
                }

                // Будем собирать все сообщения, готовые к отправке на конкретные токены
                var fcmMessagesToSend = new List<Message>();
                // Список токенов на удаление, если Firebase вернет ошибку Unregistered
                //var deadTokens = new List<DeviceToken>();

                // Группируем по чатам, чтобы оптимизировать запросы к ConversationMembers
                var batchByConversations = batchToSend.GroupBy(x => x.ConversationId);

                foreach (var conversationGroup in batchByConversations)
                {
                    var conversationId = conversationGroup.Key;

                    // 1. Быстрый in-memory кэш чатов
                    var cachedChat = await _chatCache.GetConversationAsync(conversationId);
                    if (cachedChat == null) continue;

                    // Собираем всех потенциальных получателей для ВСЕХ сообщений в этом чате из текущего батча
                    var senderIds = conversationGroup.Select(x => x.SenderId).Distinct().ToList();
                    var potentialRecipients = cachedChat.Members.Where(id => !senderIds.Contains(id)).ToList();
                    if (!potentialRecipients.Any()) continue;

                    // 4. Пачкой запрашиваем из базы настройки мьюта для участников этого чата
                    var memberSettings = await db.ConversationMembers
                        .Where(cm => cm.ConversationId == conversationId && potentialRecipients.Contains(cm.UserId))
                        .Select(cm => new { cm.UserId, cm.IsMuted, cm.UnreadCount })
                        .ToDictionaryAsync(cm => cm.UserId, ct);

                    // Обрабатываем каждое сообщение внутри этого чата
                    foreach (var task in conversationGroup)
                    {
                        // 2. Превью текста
                        string bodyText = task.MessageType.ToLower() switch
                        {
                            "image" => "📷 Фото",
                            "video" => "📹 Видео",
                            "voice" => "🎤 Голосовое сообщение",
                            "file" => "📎 Файл",
                            _ => task.MessageText
                        };

                        // 3. Формируем заголовки
                        string senderName = _userCache.GetDisplayName(task.SenderId);
                        string pushTitle = senderName;
                        string pushBody = bodyText;

                        if (cachedChat.Type != ConversationType.direct)
                        {
                            pushTitle = cachedChat.Name;
                            pushBody = $"{senderName}: {bodyText}";
                        }

                        // Фильтрация получателей конкретного сообщения
                        var finalRecipientIds = new List<Guid>();
                        foreach (var recipientId in potentialRecipients)
                        {
                            // Не шлем автору этого конкретного сообщения
                            if (recipientId == task.SenderId) continue;

                            bool isOnline = _userCache.IsOnline(recipientId);
                            if (isOnline)
                            {
                                // 2. Если онлайн, узнаем, какой чат у него сейчас открыт на экране
                                Guid? activeChatId = _userCache.GetCurrentChatId(recipientId);

                                // УСЛОВИЕ ФРОНТА: Приложение на переднем плане и открыт ИМЕННО ЭТОТ чат -> пуш НЕ шлем
                                if (activeChatId == conversationId)
                                {
                                    continue; // Сообщение прилетит живьем по вебсокету
                                }
                            }

                            bool isMuted = memberSettings.TryGetValue(recipientId, out var settings) && settings.IsMuted;
                            bool isMentioned = task.TargetUserIds.Contains(recipientId); // В TargetUserIds мы передали validMentionIds

                            if (isMuted && !isMentioned)
                                continue;

                            finalRecipientIds.Add(recipientId);
                        }

                        if (!finalRecipientIds.Any()) continue;

                        // 5. Выгрузка токенов устройств для прошедших фильтр
                        var devices = await db.DeviceTokens
                            .Where(d => finalRecipientIds.Contains(d.UserId))
                            .ToListAsync(ct);

                        if (!devices.Any()) continue;

                        // 6. Формируем контракты данных Firebase
                        foreach (var device in devices)
                        {
                            int userUnreadCount = memberSettings.TryGetValue(device.UserId, out var s) ? s.UnreadCount : 0;

                            var fcmMessage = new Message()
                            {
                                Token = device.Token,
                                Notification = new Notification()
                                {
                                    Title = pushTitle,
                                    Body = pushBody
                                },
                                Data = new Dictionary<string, string>()
                                {
                                    { "chatId", conversationId.ToString() },
                                    { "messageId", task.MessageId.ToString() },
                                    { "type", "message" },
                                    { "unreadCount", userUnreadCount.ToString() }
                                },
                                Android = new AndroidConfig()
                                {
                                    Priority = Priority.High
                                }
                            };

                            // Добавляем сообщение в общий пул на отправку и связываем с девайсом для потенциального удаления
                            fcmMessagesToSend.Add(fcmMessage);
                        }
                    }
                }

                // Отправляем все сформированные сообщения
                if (fcmMessagesToSend.Count > 0)
                {
                    var messagingInstance = FirebaseMessaging.DefaultInstance;

                    // Для сохранения логики чистки токенов на лету при массовой отправке,
                    // мы используем SendAllAsync. Он возвращает BatchResponse, где порядок результатов совпадает с порядком сообщений.
                    foreach (var chunk in fcmMessagesToSend.Chunk(500))
                    {
                        BatchResponse batchResponse = await messagingInstance.SendEachAsync(chunk, ct);
                        bool needsDbSave = false;
                        var deadTokenStrings = new List<string>();

                        for (int i = 0; i < batchResponse.Responses.Count; i++)
                        {
                            var response = batchResponse.Responses[i];
                            if (!response.IsSuccess && response.Exception != null)
                            {
                                var ex = response.Exception;
                                var failedMessage = chunk[i];
                                if (ex.MessagingErrorCode == MessagingErrorCode.Unregistered || ex.MessagingErrorCode == MessagingErrorCode.InvalidArgument)
                                {
                                    _logger.LogWarning("Токен устройства устарел: {Token}. Пометка на удаление...", failedMessage.Token);

                                    deadTokenStrings.Add(failedMessage.Token);
                                    //// Ищем токен в бд по значению строки, чтобы удалить
                                    //var deadTokenObj = await db.DeviceTokens.FirstOrDefaultAsync(d => d.Token == failedMessage.Token, ct);
                                    //if (deadTokenObj != null)
                                    //{
                                    //    db.DeviceTokens.Remove(deadTokenObj);
                                    //    needsDbSave = true;
                                    //}
                                }
                                else
                                {
                                    _logger.LogError(ex, "Ошибка отправки пуша на токен {Token}", failedMessage.Token);
                                }
                            }
                        }

                        if (deadTokenStrings.Any()) 
                        { 
                            var tokensToRemove = await db.DeviceTokens.Where(d => deadTokenStrings.Contains(d.Token)).ToListAsync(ct); 
                            if (tokensToRemove.Any()) 
                            { 
                                db.DeviceTokens.RemoveRange(tokensToRemove); 
                                await db.SaveChangesAsync(ct); 
                            } 
                        }

                        //// Сохраняем пачкой все удаления "мёртвых" токенов, если они были зафиксированы
                        //if (needsDbSave)
                        //{
                        //    await db.SaveChangesAsync(ct);
                        //}
                    }
                }

                _queue.OnBatchConsumed(batchToSend.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка обработки пакета PUSH-уведомлений");
            }
        }
    }
}
