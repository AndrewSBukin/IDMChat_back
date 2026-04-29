using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IDMChat.Models
{
    [Index(nameof(UserId), nameof(IsPinned), nameof(ConversationId))]
    [Index(nameof(UserId), nameof(JoinedAt))]
    [PrimaryKey(nameof(ConversationId), nameof(UserId))]
    public class ConversationMember
    {
        public Guid ConversationId { get; set; }

        public Guid UserId { get; set; }

        [Required]
        public bool IsAdmin { get; set; }

        [Required]
        public bool IsPinned { get; set; }

        [Required]
        public bool IsMuted { get; set; }

        [Required]
        public int UnreadCount { get; set; }

        [Required]
        public DateTime JoinedAt { get; set; }

        public Guid? LastReadMessageId { get; set; }

        [ForeignKey(nameof(ConversationId))]
        public Conversation Conversation { get; set; } = null!;
    }
}
