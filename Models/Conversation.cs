using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IDMChat.Models
{
    [Index(nameof(UpdatedAt))]
    [Index(nameof(Type))]
    public class Conversation
    {
        [Key]
        public Guid Id { get; set; }

        [Required]
        [Column(TypeName = "varchar(10)")]
        public ConversationType Type { get; set; }

        [MaxLength(100)]
        public string? Name { get; set; }

        [MaxLength(500)]
        public string? AvatarUrl { get; set; }

        [Required]
        public DateTime CreatedAt { get; set; }

        [Required]
        public DateTime UpdatedAt { get; set; }

        [Required]
        public bool IsWriteRestricted { get; set; } = false; // true = только админы

        #region Last message
        public Guid? LastMessageId { get; set; }

        [MaxLength(2000)]
        public string? LastMessageText { get; set; }

        public Guid? LastMessageSenderId { get; set; }

        public DateTime? LastMessageCreatedAt { get; set; }
        #endregion

        [ForeignKey(nameof(LastMessageId))]
        public Message? LastMessage { get; set; }

        public ICollection<ConversationMember> Members { get; set; } = [];
    }

    public enum ConversationType
    {
        [Display(Name = "direct")]
        Direct = 0,

        [Display(Name = "group")]
        Group = 1
    }
}
