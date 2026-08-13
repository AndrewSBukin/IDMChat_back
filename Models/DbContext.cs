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
        public DbSet<ExternalChatMapping> ExternalChatMappings { get; set; }
        public DbSet<Section> Sections { get; set; }
        public DbSet<Permission> Permissions { get; set; }
        public DbSet<Role> Roles { get; set; }
        public DbSet<RoleSection> RoleSections { get; set; }
        public DbSet<RolePermission> RolePermissions { get; set; }
        public DbSet<UserSectionOverride> UserSectionOverrides { get; set; }
        public DbSet<UserPermissionOverride> UserPermissionOverrides { get; set; }
        public DbSet<UserLimit> UserLimits { get; set; }
        public DbSet<UserClub> UserClubs { get; set; }
        public DbSet<UserProfile> UserProfiles { get; set; }
        public DbSet<Club> Clubs { get; set; }

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


            modelBuilder.Entity<Section>()
            .HasOne(s => s.Parent)
            .WithMany(p => p.Children)
            .HasForeignKey(s => s.ParentKey)
            .OnDelete(DeleteBehavior.Restrict);

            // 2. Составные ключи для шаблонов ролей
            modelBuilder.Entity<RoleSection>()
                .HasKey(rs => new { rs.RoleId, rs.SectionKey });

            modelBuilder.Entity<RolePermission>()
                .HasKey(rp => new { rp.RoleId, rp.PermissionKey });

            // 3. Составные ключи для пер-юзер оверрайдов и лимитов
            modelBuilder.Entity<UserSectionOverride>()
                .HasKey(uso => new { uso.UserId, uso.SectionKey });

            modelBuilder.Entity<UserPermissionOverride>()
                .HasKey(upo => new { upo.UserId, upo.PermissionKey });

            modelBuilder.Entity<UserLimit>()
                .HasKey(ul => new { ul.UserId, ul.LimitKey });

            modelBuilder.Entity<UserClub>()
                .HasKey(uc => new { uc.UserId, uc.ClubId });

            // 4. Оптимизация производительности: Индексы для 10k+ нагрузок
            // Когда пользователь логинится, мы ищем все его оверрайды по UserId. Индексы делают этот поиск мгновенным.
            modelBuilder.Entity<UserSectionOverride>()
                .HasIndex(uso => uso.UserId)
                .HasDatabaseName("IX_UserSectionOverrides_UserId");

            modelBuilder.Entity<UserPermissionOverride>()
                .HasIndex(upo => upo.UserId)
                .HasDatabaseName("IX_UserPermissionOverrides_UserId");

            modelBuilder.Entity<UserLimit>()
                .HasIndex(ul => ul.UserId)
                .HasDatabaseName("IX_UserLimits_UserId");

            modelBuilder.Entity<UserClub>()
                .HasIndex(uc => uc.UserId)
                .HasDatabaseName("IX_UserClubs_UserId");

            modelBuilder.Entity<Club>(entity =>
            {
                entity.HasKey(c => c.Id);

                // ЖЕСТКОЕ ОТКЛЮЧЕНИЕ АВТОИНКРЕМЕНТА:
                entity.Property(c => c.Id)
                      .ValueGeneratedNever();
            });
        }
    }
}
