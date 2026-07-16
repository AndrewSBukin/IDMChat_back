using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IDMChat.Models
{
    [Table("ExternalChatMappings")]
    // Уникальный индекс для мгновенного поиска по внешнему ID
    [Index(nameof(ExternalChatId), IsUnique = true, Name = "IX_ExternalChatMappings_ExternalChatId")]
    public class ExternalChatMapping
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        [MaxLength(64)]
        public string ExternalChatId { get; set; } = string.Empty; // Сюда пишем "tg_-100123456" или строковые хардкод-ключи из ИДМ

        [Required]
        public Guid ConversationId { get; set; } // Внутренний Guid чата в мессенджере

        [ForeignKey(nameof(ConversationId))]
        public Conversation Conversation { get; set; } = null!;

        [Required]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
