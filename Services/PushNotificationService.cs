using FirebaseAdmin.Messaging;
using IDMChat.Utils;
using IDMChat.Models;
using Microsoft.EntityFrameworkCore;

namespace IDMChat.Services
{
    public interface IPushNotificationService
    {
        Task SendNewMessagePushAsync(Models.Message message, List<Guid> mentionedUserIds, string rawType);
    }


    public class PushNotificationService: IPushNotificationService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly UserCache _userCache;
        private readonly ChatStateCache _chatCache; // Ваш кэш состояния чатов
        private readonly ILogger<PushNotificationService> _logger;

        public PushNotificationService(IServiceProvider serviceProvider, UserCache userCache, ChatStateCache chatCache, ILogger<PushNotificationService> logger)
        {
            _serviceProvider = serviceProvider;
            _userCache = userCache;
            _chatCache = chatCache;
            _logger = logger;
        }

        public async Task SendNewMessagePushAsync(Models.Message message, List<Guid> mentionedUserIds, string rawType)
        {
            try
            {
                // Так как мы будем вызывать этот метод асинхронно из фоновой очереди (BackgroundLogQueue), 
                // создаем Scope для безопасной работы с DbContext
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

                var conversationId = message.ConversationId;
                var senderId = message.SenderId;

                // 1. Получаем состояние чата из вашего быстрого in-memory кэша чатов
                var cachedChat = await _chatCache.GetConversationAsync(conversationId);
                if (cachedChat == null) return;

                // Исключаем отправителя сообщения из получателей пуша
                var potentialRecipients = cachedChat.Members.Where(id => id != senderId).ToList();
                if (!potentialRecipients.Any()) return;

                // 2. Формируем превью текста (телом) пуша в зависимости от типа
                string bodyText = rawType.ToLower() switch
                {
                    "image" => "📷 Фото",
                    "video" => "📹 Видео",
                    "voice" => "🎤 Голосовое сообщение",
                    "file" => "📎 Файл",
                    _ => message.Text // Для обычного текста
                };

                // 3. Формируем заголовок и тело в зависимости от типа чата (директ или группа)
                string senderName = _userCache.GetDisplayName(senderId);
                string pushTitle = senderName;
                string pushBody = bodyText;

                if (cachedChat.Type != ConversationType.direct)
                {
                    pushTitle = cachedChat.Name; // Имя группы в заголовок
                    pushBody = $"{senderName}: {bodyText}"; // В теле "Имя: текст"
                }

                // 4. ФИЛЬТРАЦИЯ ПОЛУЧАТЕЛЕЙ (Мьют, Онлайн в конкретном чате)
                var finalRecipientIds = new List<Guid>();

                // Запрашиваем из базы настройки мьюта для участников пачкой
                var memberSettings = await db.ConversationMembers
                    .Where(cm => cm.ConversationId == conversationId && potentialRecipients.Contains(cm.UserId))
                    .Select(cm => new { cm.UserId, cm.IsMuted, cm.UnreadCount })
                    .ToDictionaryAsync(cm => cm.UserId);

                foreach (var recipientId in potentialRecipients)
                {
                    // УСЛОВИЕ: Если пользователь вообще не в сети (нет сокет-соединения)
                    // мы ОДНОЗНАЧНО шлем ему пуш.
                    if (!_userCache.IsOnline(recipientId))
                    {
                        finalRecipientIds.Add(recipientId);
                        continue;
                    }

                    bool isMuted = memberSettings.TryGetValue(recipientId, out var settings) && settings.IsMuted;
                    bool isMentioned = mentionedUserIds.Contains(recipientId);

                    // УСЛОВИЕ: Если чат замьючен - пуш не шлем. Исключение - @-упоминание
                    if (isMuted && !isMentioned)
                        continue;

                    finalRecipientIds.Add(recipientId);
                }

                if (!finalRecipientIds.Any()) return;

                // 5. ВЫГРУЗКА ТОКЕНОВ УСТРОЙСТВ
                var devices = await db.DeviceTokens
                    .Where(d => finalRecipientIds.Contains(d.UserId))
                    .ToListAsync();

                if (!devices.Any()) return;

                // 6.МАССОВАЯ ОТПРАВКА ЧЕРЕЗ FIREBASE ADMIN SDK
                foreach (var device in devices)
                {
                    // Узнаем unreadCount конкретного юзера для бейджа приложения
                    int userUnreadCount = memberSettings.TryGetValue(device.UserId, out var s) ? s.UnreadCount : 0;

                    var fcmMessage = new FirebaseAdmin.Messaging.Message()
                    {
                        Token = device.Token,
                        Notification = new Notification()
                        {
                            Title = pushTitle,
                            Body = pushBody
                        },
                        // СТРОГИЙ КОНТРАКТ ДАННЫХ ДЛЯ ВАШЕГО ФРОНТЕНДА (Менять ключи нельзя!)
                        Data = new Dictionary<string, string>()
                    {
                        { "chatId", conversationId.ToString() },
                        { "messageId", message.Id.ToString() },
                        { "type", "message" },
                        { "unreadCount", userUnreadCount.ToString() } // Опциональный бейдж
                    },
                        Android = new AndroidConfig()
                        {
                            Priority = Priority.High // Высокий приоритет для мгновенной доставки
                        }
                    };

                    try
                    {
                        // Отправляем пуш на конкретное устройство
                        string response = await FirebaseMessaging.DefaultInstance.SendAsync(fcmMessage);
                    }
                    catch (FirebaseMessagingException ex) when (
                        ex.MessagingErrorCode == MessagingErrorCode.Unregistered ||
                        ex.MessagingErrorCode == MessagingErrorCode.InvalidArgument)
                    {
                        // УСЛОВИЕ: Чистка мёртвых токенов (UNREGISTERED / INVALID_ARGUMENT)
                        _logger.LogWarning("Токен устройства {DeviceId} устарел. Удаление из БД...", device.DeviceId);
                        db.DeviceTokens.Remove(device);
                        await db.SaveChangesAsync(); // Фиксируем чистку «на лету»
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ошибка отправки пуша на устройство {DeviceId}", device.DeviceId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error sending PUSH: {ex.Message}");
            }
        }
    }
}
