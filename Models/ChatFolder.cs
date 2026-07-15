using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IDMChat.Models
{
    [Table("ChatFolders")]
    [Index(nameof(UserId), nameof(Position), IsUnique = true)] // Position уникален для юзера
    public class ChatFolder
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid UserId { get; set; }

        [Required]
        [MaxLength(24)] // Ограничение из ТЗ фронта
        public string Title { get; set; } = string.Empty;

        [Required]
        public int Position { get; set; }

        [Required]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public List<ChatFolderItem> Items { get; set; } = new();
    }

    [Table("ChatFolderItems")]
    [PrimaryKey(nameof(FolderId), nameof(ConversationId))]
    public class ChatFolderItem
    {
        public Guid FolderId { get; set; }
        [ForeignKey(nameof(FolderId))]
        public ChatFolder Folder { get; set; } = null!;

        public Guid ConversationId { get; set; }
        [ForeignKey(nameof(ConversationId))]
        public Conversation Conversation { get; set; } = null!;

        // Порядок обычного добавления чата в папку
        [Required]
        public int Order { get; set; }

        // Логика закрепа ВНУТРИ конкретного таба (Пункт 2.8 ТЗ)
        [Required]
        public bool IsPinned { get; set; } = false;

        [Required]
        public int PinnedOrder { get; set; } = 0; // Порядок среди закрепленных (0..4)
    }
}
