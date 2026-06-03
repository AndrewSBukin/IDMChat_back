using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace IDMChat.Models
{
    [PrimaryKey(nameof(MessageId), nameof(UserId))]
    public class MessageReadReceipt
    {
        public long MessageId { get; set; }

        public Guid UserId { get; set; }

        public DateTime ReadAt { get; set; }
    }
}
