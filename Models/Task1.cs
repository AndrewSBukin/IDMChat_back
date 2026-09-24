using System.Text.Json.Serialization;

namespace IDMChat.Models
{
    public abstract record BaseTask
    {
        public Guid ConversationId { get; set; }
    }

    public record Task1 : BaseTask // PushNotificationTask
    {
        public Guid SenderId { get; set; }
        public string MessageText { get; set; } = string.Empty;
        public string MessageType { get; set; } = string.Empty;
        public List<Guid> TargetUserIds { get; set; } = new List<Guid>();
        public long MessageId { get; internal set; }
        public List<Guid> MentionedUserIds { get; set; } = new();
    }

    public record Task2 : BaseTask // SignalRMentionTask
    {
        public List<Guid> TargetUserIds { get; set; }
    }

    public record Task3 : BaseTask // BotCommandTask
    {
        public Guid SenderId { get; set; }
        public string MessageText { get; set; } = string.Empty;
        public string MessageType { get; set; } = string.Empty;
        public List<Guid> TargetUserIds { get; set; } = new List<Guid>();
        public long MessageId { get; internal set; }
        public List<Guid> MentionedUserIds { get; set; } = new();
    }
}
