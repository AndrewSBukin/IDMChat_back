using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace IDMChat.Models
{
    [PrimaryKey(nameof(MessageId), nameof(Url))] 
    public class MessageLink
    {
        public long MessageId { get; set; }

        [ForeignKey(nameof(MessageId))]
        public virtual Message Message { get; set; } = null!;

        public Guid ConversationId { get; set; } // Добавляем для моментальной выборки по ID чата

        public string Url { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }
    }
}