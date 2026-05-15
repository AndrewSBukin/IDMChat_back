using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IDMChat.Models
{
    public class Attachment
    {
        [Key]
        public Guid Id { get; set; }

        [Required]
        public long MessageId { get; set; }

        [Required]
        [MaxLength(255)]
        public string FileName { get; set; } = string.Empty;

        [Required]
        public long FileSize { get; set; }

        [Required]
        [MaxLength(100)]
        public string MimeType { get; set; } = string.Empty;

        [Required]
        [MaxLength(500)]
        public string StoragePath { get; set; } = string.Empty;

        [MaxLength(500)]
        public string? ThumbnailPath { get; set; }

        [ForeignKey(nameof(MessageId))]
        public Message Message { get; set; } = null!;
    }
}
