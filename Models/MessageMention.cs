using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace IDMChat.Models
{
    [PrimaryKey(nameof(MessageId), nameof(UserId))]
    public class MessageMention
    {
        public long MessageId { get; set; }

        [ForeignKey(nameof(MessageId))]
        public virtual Message Message { get; set; } = null!;

        public Guid UserId { get; set; }

        [ForeignKey(nameof(UserId))]
        public virtual User User { get; set; } = null!;

        public string DisplayName { get; set; }
    }
}
