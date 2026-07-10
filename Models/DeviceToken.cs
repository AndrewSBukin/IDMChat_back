using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace IDMChat.Models
{
    [PrimaryKey(nameof(UserId), nameof(DeviceId))] // Защита от дубликатов устройств
    public class DeviceToken
    {
        public Guid UserId { get; set; }

        [Required]
        public string DeviceId { get; set; } = string.Empty;

        [Required]
        public string Token { get; set; } = string.Empty;

        [Required]
        public string Platform { get; set; } = string.Empty; // "android" | "ios"

        public DateTime UpdatedAt { get; set; }
    }
}
