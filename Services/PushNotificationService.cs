using FirebaseAdmin.Messaging;
using IDMChat.Models;
using IDMChat.Utils;
using Microsoft.EntityFrameworkCore;
using System.Collections;

namespace IDMChat.Services
{
    public interface IPushNotificationService
    {
        Task SendNewMessagePush(Models.Message message, List<Guid> mentionedUserIds, string rawType);
    }


    public class PushNotificationService: IPushNotificationService
    {
        private readonly ILogger<PushNotificationService> _logger;
        private readonly IBackgroundPushQueue _backgroundQueue;


        public PushNotificationService(ILogger<PushNotificationService> logger, IBackgroundPushQueue backgroundQueue)
        {
            _logger = logger;
            _backgroundQueue = backgroundQueue;
        }

        public Task SendNewMessagePush(Models.Message message, List<Guid> mentionedUserIds, string rawType)
        {
            _backgroundQueue.Enqueue(new PushNotificationTask
            {
                MessageId = message.Id,
                ConversationId = message.ConversationId,
                SenderId = message.SenderId,
                MessageText = message.Text ?? string.Empty,
                MessageType = rawType,
                TargetUserIds = mentionedUserIds ?? new List<Guid>() // Сюда передаются меншены
            });

            return Task.CompletedTask;
        }
    }
}
