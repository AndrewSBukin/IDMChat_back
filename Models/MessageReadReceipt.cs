using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace IDMChat.Models
{
    [Index(nameof(MessageId), nameof(UserId))]
    public class MessageReadReceipt
    {
        [Key]
        public long MessageId { get; set; }

        public Guid UserId { get; set; }

        public DateTime ReadAt { get; set; }
    }
}
