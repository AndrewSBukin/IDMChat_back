using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IDMChat.Models
{
    public enum ChatRole
    {
        Member = 10,       // Участник: может только писать сообщения
        Admin = 20,        // Админ: может удалять чужие сообщения, банить участников
        Owner = 30         // Владелец: полный контроль, может назначить админа или удалить чат
    }

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
        public int PinnedOrder { get; set; } = 0;

        [Required]
        public bool IsMuted { get; set; }

        [Required]
        public int UnreadCount { get; set; }

        [Required]
        public DateTime JoinedAt { get; set; }

        public long? LastReadMessageId { get; set; }

        public ChatRole Role { get; set; }


        [ForeignKey(nameof(ConversationId))]
        public Conversation Conversation { get; set; } = null!;

        [ForeignKey(nameof(UserId))]
        public User User { get; set; } = null!;
    }
}
