using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IDMChat.Models
{
    [Table("Messages")]
    [Index(nameof(ChatId), nameof(SentAt))]
    [Index(nameof(SentAt))]
    public class Message
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid ChatId { get; set; }

        [Required]
        public Guid UserId { get; set; }

        [Required]
        [MaxLength(5000)]
        public string Text { get; set; } = string.Empty;

        [Required]
        public DateTime SentAt { get; set; } = DateTime.UtcNow;

        // Для оптимизации - индексное поле
        public bool IsRead { get; set; } = false;

        // Для мягкого удаления
        public bool IsDeleted { get; set; } = false;
    }
}
