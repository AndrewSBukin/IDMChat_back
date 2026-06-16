using IDMChat.Models;

namespace IDMChat.DTO
{
    public class MessageDto
    {
        public long id { get; set; }
        public Guid conversation_id { get; set; }

        public Guid sender_id { get; set; }
        public UserBriefDto sender { get; set; } = null!;

        public string type { get; set; } = string.Empty;
        public string? text { get; set; }
        public List<AttachmentDto>? attachments { get; set; }

        public ReplyPreviewDto? reply_to { get; set; }
        public long? reply_to_id { get; set; }

        public bool is_edited { get; set; }
        public bool is_deleted { get; set; }
        public DateTime created_at { get; set; }
        public DateTime? updated_at { get; set; }

        public int read_count { get; set; }
        // ⚠️ Для личных чатов, для групп от 5 человек - null
        public List<UserBriefDto>? read_by { get; set; }
    }

    public class AttachmentDto
    {
        public Guid id { get; set; }
        public string file_name { get; set; } = string.Empty;
        public long file_size { get; set; }
        public string mime_type { get; set; } = string.Empty;
        public string url { get; set; } = string.Empty;
        public string? thumbnail_url { get; set; }
    }
    public class LastMessageDto
    {
        public long id { get; internal set; }
        public string text { get; internal set; }
        public string type { get; internal set; }
        public Guid sender_id { get; internal set; }
        public string sender_name { get; internal set; }
        public DateTime created_at { get; internal set; }
        public List<AttachmentDto> attachments { get; internal set; }
    }
    public class ConversationUpdatedDto
    {
        public Guid id { get; internal set; }
        public string type { get; internal set; }
        public string? name { get; internal set; }
        public string? avatar_url { get; internal set; }
        public DateTime updated_at { get; internal set; }
        public LastMessageDto? last_message { get; internal set; }
    }
    public class ReplyPreviewDto
    {
        public long id { get; set; }
        public Guid sender_id { get; set; }
        public string sender_name { get; set; } = string.Empty;
        public string text { get; set; } = string.Empty;
        public string type { get; set; } = string.Empty; // text, image, etc.
    }

    public class UserBriefDto
    {
        public Guid id { get; set; }
        public string display_name { get; set; } = string.Empty;
        public string? avatar_url { get; set; }
    }
}
