using IDMChat.DTO;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IDMChat.Models
{
    public class Section
    {
        [Key]
        [MaxLength(100)]
        public string Key { get; set; } = null!; // Напр., "office.staff", "app.chat"

        [MaxLength(20)]
        public string Scope { get; set; } = null!; // "app" или "club"

        [MaxLength(150)]
        public string Title { get; set; } = null!;

        [MaxLength(50)]
        public string Icon { get; set; } = null!; // Семантический ключ иконки ("home")

        public int Order { get; set; }

        [MaxLength(100)]
        public string? ParentKey { get; set; }

        [ForeignKey(nameof(ParentKey))]
        public Section? Parent { get; set; }

        public ICollection<Section> Children { get; set; } = new List<Section>();

        public bool IsActive { get; set; } = true;
    }

    public class Permission
    {
        [Key]
        [MaxLength(100)]
        public string Key { get; set; } = null!; // Напр., "daily.expense.edit"

        [MaxLength(250)]
        public string Description { get; set; } = null!;
    }

    public class Role
    {
        [Key]
        public Guid Id { get; set; }

        [MaxLength(50)]
        public string Code { get; set; } = null!; // "manager", "cashier"

        [MaxLength(100)]
        public string Name { get; set; } = null!;
    }

    public class RoleSection
    {
        public Guid RoleId { get; set; }
        public Role Role { get; set; } = null!;

        [MaxLength(100)]
        public string SectionKey { get; set; } = null!;
        public Section Section { get; set; } = null!;
    }

    public class RolePermission
    {
        public Guid RoleId { get; set; }
        public Role Role { get; set; } = null!;

        [MaxLength(100)]
        public string PermissionKey { get; set; } = null!;
        public Permission Permission { get; set; } = null!;
    }

    public class UserProfile
    {
        [Key]
        public Guid UserId { get; set; } // FK на вашу существующую таблицу Users

        public Guid? RoleId { get; set; }
        [ForeignKey(nameof(RoleId))]
        public Role? Role { get; set; }

        [MaxLength(100)]
        public string? DefaultSectionKey { get; set; }
        [ForeignKey(nameof(DefaultSectionKey))]
        public Section? DefaultSection { get; set; }

        [MaxLength(100)]
        public string? ClubLandingSectionKey { get; set; }
        [ForeignKey(nameof(ClubLandingSectionKey))]
        public Section? ClubLandingSection { get; set; }
    }

    public class UserSectionOverride
    {
        public Guid UserId { get; set; }

        [MaxLength(100)]
        public string SectionKey { get; set; } = null!;
        public Section Section { get; set; } = null!;

        public AccessEffect Effect { get; set; }
    }

    public class UserPermissionOverride
    {
        public Guid UserId { get; set; }

        [MaxLength(100)]
        public string PermissionKey { get; set; } = null!;
        public Permission Permission { get; set; } = null!;

        public AccessEffect Effect { get; set; }
    }

    public class UserLimit
    {
        public Guid UserId { get; set; }

        [MaxLength(100)]
        public string LimitKey { get; set; } = null!; // Напр., "daily.history.days"

        public int IntValue { get; set; }
    }

    public class UserClub
    {
        public Guid UserId { get; set; }

        public int ClubId { get; set; } // FK на вашу существующую таблицу Clubs
    }
}
