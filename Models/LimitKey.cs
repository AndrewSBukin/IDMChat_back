using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IDMChat.Models
{
    public class LimitKey
    {
        [Key]
        [MaxLength(128)]
        public string Key { get; set; } = null!;

        [MaxLength(400)]
        public string Description { get; set; } = null!;

        [MaxLength(32)]
        public string Unit { get; set; } = null!;
    }
}
