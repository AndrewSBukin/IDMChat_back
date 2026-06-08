using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IDMChat.Models
{
    [PrimaryKey(nameof(MessageId), nameof(UserId))]
    public class MessageReadReceipt
    {
        public long MessageId { get; set; }

        public Guid UserId { get; set; }

        public DateTime ReadAt { get; set; }

        [ForeignKey(nameof(UserId))]
        public User User { get; set; } = null!;
    }
}
