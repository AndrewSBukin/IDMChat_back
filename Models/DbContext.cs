using Microsoft.EntityFrameworkCore;

namespace IDMChat.Models
{
    public class ChatDbContext : DbContext
    {
        public ChatDbContext(DbContextOptions<ChatDbContext> options)
            : base(options)
        {
        }

        public DbSet<Message> Messages { get; set; }
        public DbSet<User> Users { get; set; }
        public DbSet<RefreshToken> RefreshTokens { get; set; }
        public DbSet<Conversation> Conversations { get; set; }
        public DbSet<ConversationMember> ConversationMembers { get; set; }
        public DbSet<MessageReadReceipt> MessageReadReceipts { get; set; }
        public DbSet<MessageMention> MessageMentions { get; set; }
        public DbSet<MessageLink> MessageLinks { get; set; }
        public DbSet<FileAttachment> FileAttachments { get; set; }
        public DbSet<DeviceToken> DeviceTokens { get; set; }
        public DbSet<MessageReaction> MessageReactions { get; set; }
        public DbSet<ChatFolder> ChatFolders { get; set; }
        public DbSet<ChatFolderItem> ChatFolderItems { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<FileAttachment>()
                .HasOne(f => f.User)
                .WithMany()
                .HasForeignKey(f => f.UserId)
                .OnDelete(DeleteBehavior.NoAction);
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<MessageReaction>()
                .Property(r => r.Emoji)
                .UseCollation("Latin1_General_BIN2");

            modelBuilder.Entity<Message>()
                .HasIndex(m => new { m.ConversationId, m.IsPinned, m.PinnedAt })
                .HasDatabaseName("IX_Messages_Conversation_Pinned")
                .HasFilter("[IsPinned] = 1");
        }
    }
}
