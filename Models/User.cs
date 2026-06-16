using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data;

namespace IDMChat.Models
{
    [Table("Users")]
    [Index(nameof(Username), IsUnique = true)]
    [Index(nameof(idm))]
    [Index(nameof(Email))]
    [Index(nameof(Phone))]
    [Index(nameof(IsOnline))]
    public class User
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        [MaxLength(100)]
        public string DisplayName { get; set; } = string.Empty;

        [MaxLength(500)]
        public string? AvatarUrl { get; set; }

        [Required]
        [MaxLength(100)]
        public string Username { get; set; } = string.Empty;

        [Required]
        public string PasswordHash { get; set; } = string.Empty;

        public UserPresenceStatus Status { get; set; } // online, offline, away

        [MaxLength(8)]
        public string? idm { get; set; }
        public UserRole Role { get; set; } = UserRole.Employee;

        [Required]
        [MaxLength(200)]
        public string ConnectionId { get; set; } = string.Empty;

        public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
        /// <summary>
        /// deprecated
        /// </summary>
        public bool IsOnline { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastLoginAt { get; set; }
        
        public bool IsActive { get; set; } = true;
        
        [MaxLength(50)]
        public string Phone { get; set; } = string.Empty;
        
        [Required]
        [MaxLength(100)]
        public string? Email { get; set; } = string.Empty;

        public string? CustomStatus { get; set; } // "в коде", "сплю" и т.д.

        // Настройки
        public bool? NotificationsEnabled { get; set; } = true;
        public bool? SoundEnabled { get; set; } = true;

        [MaxLength(10)]
        public string? Language { get; set; } = "ru";

        [MaxLength(10)]
        public string? Theme { get; set; } = "system";

        [InverseProperty(nameof(RefreshToken.User))]
        public virtual ICollection<RefreshToken> RefreshTokens { get; set; }
    }

    public enum UserRole
    {
        Employee = 0,
        Manager = 1,
        Developer = 2,
        Admin = 3
    }

    public enum UserPresenceStatus
    {
        Online,
        Offline,
        Away,
        DoNotDisturb
    }
}
