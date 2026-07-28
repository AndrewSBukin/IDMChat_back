using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IDMChat.Models
{
    public class Club
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        [Required]
        [MaxLength(100)]
        public string Name { get; set; } = null!; // "АКС1828"

        [Required]
        [MaxLength(50)]
        public string Code { get; set; } = null!; // "1828" (это ваш bbID/бизнес-номер для контракта)

        [Required]
        [MaxLength(150)]
        public string Idm { get; set; } = null!; // idm (aka Company)

        // Денормализованные поля города (вместо отдельной таблицы городов)
        [Required]
        [MaxLength(100)]
        public string CityName { get; set; } = null!; // "Ростов-на-Дону"

        public int CityGmt { get; set; } // Смещение таймзоны, например: 3
    }


}
