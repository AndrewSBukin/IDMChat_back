using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IDMChat.Models
{
    public class IdmRoleMap
    {
        [Key]
        [MaxLength(64)]
        public string IdmRole { get; set; } = null!;

        [Required]
        [MaxLength(64)]
        public string RoleCode { get; set; } = null!;

        [MaxLength(128)]
        public string DefaultSectionKey { get; set; } = null!;

        [MaxLength(128)]
        public string ClubLandingSectionKey { get; set; } = null!;

        [MaxLength(200)]
        public string Comment { get; set; } = null!;
    }
}
