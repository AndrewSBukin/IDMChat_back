using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using static IDMChat.Controllers.FilesController;

namespace IDMChat.Models
{
    public class FileAttachment
    {
        [Key]
        public Guid Id { get; set; }

        [Required]
        public long MessageId { get; set; }  // к какому сообщению привязан
        
        [Required]
        public Guid ConversationId { get; set; }

        [Required]
        public Guid UserId { get; set; }  // кто загрузил

        [Required]
        [MaxLength(255)]
        public string FileName { get; set; } = string.Empty;

        [Required]
        public long FileSize { get; set; }

        public int? Duration { get; set; }  // для видео и голосовых

        [Required]
        [MaxLength(100)]
        public string MimeType { get; set; } = string.Empty;

        [Required]
        [MaxLength(500)]
        public string StoragePath { get; set; } = string.Empty;  // относительный путь

        [MaxLength(500)]
        public string? ThumbnailPath { get; set; }

        public FileType Type { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Навигация
        [ForeignKey(nameof(UserId))]
        public User User { get; set; } = null!;

        [ForeignKey(nameof(MessageId))]
        public Message Message { get; set; } = null!;
    }
}
