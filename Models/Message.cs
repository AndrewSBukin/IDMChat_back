using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Net.Mail;
using System.Text.Json;

namespace IDMChat.Models
{
    [Table("Messages")]
    [Index(nameof(ConversationId), nameof(Id))]
    [Index(nameof(ConversationId), nameof(ClientTempId))]
    [Index(nameof(SenderId))]
    public class Message
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public long Id { get; set; }

        [Required]
        public Guid ConversationId { get; set; }

        [Required]
        public Guid SenderId { get; set; }

        [Required]
        public Guid ClientTempId { get; set; } // для дедупликации

        [Required]
        [MaxLength(5000)]
        public string Text { get; set; } = string.Empty;

        [Required]
        [Column(TypeName = "varchar(20)")]
        public MessageType Type { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAt { get; set; } = null;

        [Required]
        public DateTime SentAt { get; set; } = DateTime.UtcNow;

        public bool IsDeleted { get; set; } = false;
        public Guid? DeletedBy { get; set; } = null;
        public DateTime? DeletedAt { get; set; } = null;

        public int ChannelId { get; set; }

        public long? ReplyToMessageId { get; set; }

        //[MaxLength]
        //[Column(TypeName = "nvarchar(max)")]
        //public string? KeyboardJson { get; set; } // хранить как JSON строку

        //[NotMapped]
        //public KeyboardData? Keyboard
        //{
        //    get => KeyboardJson == null ? null : JsonSerializer.Deserialize<KeyboardData>(KeyboardJson);
        //    set => KeyboardJson = JsonSerializer.Serialize(value);
        //}

        public bool IsForwarded { get; set; }
        public Guid? OriginalSenderId { get; set; }

        /// //////////////////////////////////////////////////////////////////////////

        [ForeignKey(nameof(ReplyToMessageId))]
        public Message? ReplyToMessage { get; set; }

        public Conversation Conversation { get; set; } = null!;

        public User Sender { get; set; } = null!;

        public List<FileAttachment> FileAttachments { get; set; }

        public ICollection<MessageMention> Mentions { get; set; } = new List<MessageMention>();

        public User OriginalSender { get; set; }
        public List<MessageReaction> Reactions { get; set; } = new List<MessageReaction>();
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
        System = 3,

        [Display(Name = "video")]
        Video = 4,

        [Display(Name = "voice")]
        Voice = 5,
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
