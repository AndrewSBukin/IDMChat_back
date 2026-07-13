using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IDMChat.Models
{
    [Index(nameof(MessageId), nameof(UserId), nameof(Emoji), IsUnique = true, Name = "IX_MessageReactions_Composite")]
    [Table("MessageReactions")]
    public class MessageReaction
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public long Id { get; set; }

        [Required]
        public long MessageId { get; set; }

        [Required]
        public Guid UserId { get; set; }

        // Храним сам эмодзи как строку (например, "👍", "🔥", "🚀")
        // Строка надежнее, так как позволяет легко расширять набор эмодзи без изменения схемы БД
        [Required]
        public string Emoji { get; set; } = string.Empty;

        [Required]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;


        public Message Message { get; set; } = null!;
    }
}
