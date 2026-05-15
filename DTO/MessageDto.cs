namespace IDMChat.DTO
{
    public class MessageDto
    {
        public long Id { get; set; }
        public Guid ConversationId { get; set; }

        public Guid SenderId { get; set; }
        public UserBriefDto Sender { get; set; } = null!;

        public string Type { get; set; } = string.Empty;
        public string? Text { get; set; }
        public List<AttachmentDto>? Attachments { get; set; }

        public ReplyPreviewDto? ReplyTo { get; set; }

        public bool IsEdited { get; set; }
        public bool IsDeleted { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }

        // ⚠️ Для личных чатов, для групп - null
        public List<Guid>? ReadBy { get; set; }
    }

    public class AttachmentDto
    {
        public Guid Id { get; set; }
        public string FileName { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public string MimeType { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string? ThumbnailUrl { get; set; }
    }

    public class ReplyPreviewDto
    {
        public long Id { get; set; }
        public Guid SenderId { get; set; }
        public string SenderName { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty; // text, image, etc.
    }

    public class UserBriefDto
    {
        public Guid Id { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string? AvatarUrl { get; set; }
    }
}
