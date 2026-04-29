using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace IDMChat.Models
{
    [Table("Messages")]
    [Index(nameof(ConversationId), nameof(SentAt))]
    [Index(nameof(SentAt))]
    [Index(nameof(SenderId))]
    public class Message
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid ConversationId { get; set; }

        [Required]
        public Guid SenderId { get; set; }

        [Required]
        [MaxLength(5000)]
        public string Text { get; set; } = string.Empty;

        [Required]
        [Column(TypeName = "varchar(20)")]
        public MessageType Type { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Required]
        public DateTime SentAt { get; set; } = DateTime.UtcNow;

        public bool IsDeleted { get; set; } = false;

        public int ChannelId { get; set; }

        public Guid? ReplyToMessageId { get; set; }

        //[MaxLength]
        //[Column(TypeName = "nvarchar(max)")]
        //public string? KeyboardJson { get; set; } // хранить как JSON строку

        //[NotMapped]
        //public KeyboardData? Keyboard
        //{
        //    get => KeyboardJson == null ? null : JsonSerializer.Deserialize<KeyboardData>(KeyboardJson);
        //    set => KeyboardJson = JsonSerializer.Serialize(value);
        //}

        [ForeignKey(nameof(ReplyToMessageId))]
        public virtual Message? ReplyToMessage { get; set; }

        [ForeignKey(nameof(ConversationId))]
        public virtual Conversation Conversation { get; set; } = null!;
    }

    public enum MessageType
    {
        [Display(Name = "text")]
        Text = 0,

        [Display(Name = "image")]
        Image = 1,

        [Display(Name = "file")]
        File = 2,

        [Display(Name = "system")]
        System = 3
    }

    public class KeyboardData
    {
        public string Type { get; set; } = "inline"; // inline или reply
        public List<List<ButtonData>> Rows { get; set; } = new();
        public bool IsPersistent { get; set; }
    }

    public class ButtonData
    {
        public string Text { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty; // "command:/approve"
        public string? Url { get; set; }
        public bool RequestContact { get; set; }
    }
}
