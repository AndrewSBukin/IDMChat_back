using System.Text.Json.Serialization;

namespace IDMChat.Models
{
    public record PushNotificationTask
    {
        public Guid ConversationId { get; set; }
        public Guid SenderId { get; set; }
        public string MessageText { get; set; } = string.Empty;
        public string MessageType { get; set; } = string.Empty;
        public List<Guid> TargetUserIds { get; set; } = new List<Guid>();
        public long MessageId { get; internal set; }
        public List<Guid> MentionedUserIds { get; set; } = new();
    }
}
