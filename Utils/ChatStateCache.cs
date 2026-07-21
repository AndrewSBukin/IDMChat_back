using IDMChat.Domain;
using IDMChat.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;

namespace IDMChat.Utils
{
    public class ChatStateCache
    {
        private readonly IMemoryCache _cache;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ChatStateCache> _logger;
        private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
        private readonly MemoryCacheEntryOptions _cacheOptions;

        public ChatStateCache(IMemoryCache cache, IServiceScopeFactory scopeFactory, ILogger<ChatStateCache> logger)
        {
            _cache = cache;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _cacheOptions = new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(5),
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1)
            };
        }

        public async Task<CachedConversation> GetConversationAsync(Guid conversationId)
        {
            if (_cache.TryGetValue(conversationId, out CachedConversation cached))
                return cached;

            var semaphore = _locks.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));

            await semaphore.WaitAsync();
            try
            {
                if (_cache.TryGetValue(conversationId, out cached))
                    return cached;

                cached = await LoadFromDbAsync(conversationId);
                _cache.Set(conversationId, cached, _cacheOptions);
                return cached;
            }
            finally
            {
                semaphore.Release();
                if (_locks.TryGetValue(conversationId, out var s) && s.CurrentCount == 1)
                    _locks.TryRemove(conversationId, out _);
            }
        }

        private async Task<CachedConversation> LoadFromDbAsync(Guid conversationId)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

            var conversation = await db.Conversations
                .Include(c => c.Members)
                .ThenInclude(m => m.User)
                .FirstOrDefaultAsync(c => c.Id == conversationId);

            if (conversation == null)
                throw new NotFoundException(
                    "{\"error\": {\"code\": \"CONVERSATION_NOT_FOUND\", \"message\": \"Диалог не найден\"}}");

            var membersData = await db.ConversationMembers
                .Where(cm => cm.ConversationId == conversationId)
                .Select(cm => new { cm.UserId, cm.UnreadCount, cm.LastReadMessageId }) // Считываем поле
                .ToListAsync();

            var cached = new CachedConversation
            {
                Id = conversation.Id,
                Type = conversation.Type,
                Name = conversation.Name,
                AvatarUrl = conversation.AvatarUrl,
                IsWriteRestricted = conversation.IsWriteRestricted,
                UpdatedAt = conversation.UpdatedAt,
                LastMessageId = conversation.LastMessageId,
                LastMessageText = conversation.LastMessageText,
                LastMessageSenderId = conversation.LastMessageSenderId,
                LastMessageCreatedAt = conversation.LastMessageCreatedAt,
                Members = conversation.Members.Select(m => m.UserId).ToHashSet(),
                Admins = conversation.Members.Where(m => m.IsAdmin).Select(m => m.UserId).ToHashSet(),
                //UnreadCounts = conversation.Members.ToDictionary(m => m.UserId, m => m.UnreadCount)
            };

            foreach (var member in membersData)
            {
                //cached.Members.Add(member.UserId);
                cached.UnreadCounts[member.UserId] = member.UnreadCount;
                cached.LastReadMessageIds[member.UserId] = member.LastReadMessageId;
            }

            return cached;
        }

        public void UpdateLastMessage(Guid conversationId, Message message, string truncatedText)
        {
            if (_cache.TryGetValue(conversationId, out CachedConversation cached))
            {
                lock (cached.LockObject)
                {
                    cached.LastMessageId = message.Id;
                    cached.LastMessageText = truncatedText;
                    cached.LastMessageSenderId = message.SenderId;
                    cached.LastMessageCreatedAt = message.CreatedAt;
                    cached.UpdatedAt = message.CreatedAt;
                    //_cache.Set(conversationId, cached, _cacheOptions);
                }
            }
        }

        public void IncrementUnreadCounts(Guid conversationId, Guid excludeUserId)
        {
            if (_cache.TryGetValue(conversationId, out CachedConversation cached))
            {
                foreach (var userId in cached.Members.Where(u => u != excludeUserId))
                {
                    cached.UnreadCounts[userId] = cached.UnreadCounts.GetValueOrDefault(userId) + 1;
                }
                _cache.Set(conversationId, cached, _cacheOptions);
            }
        }

        public void ResetUnreadCount(Guid conversationId, Guid userId)
        {
            if (_cache.TryGetValue(conversationId, out CachedConversation cached))
            {
                cached.UnreadCounts[userId] = 0;
                _cache.Set(conversationId, cached, _cacheOptions);
            }
        }

        public void UpdateMuteStatus(Guid conversationId, Guid userId, bool isMuted)
        {
            // Mute статус хранится в ConversationMember, а не в CachedConversation
            // Поэтому просто инвалидируем чат, чтобы при следующей загрузке данные обновились
            Invalidate(conversationId);
        }

        public void Invalidate(Guid conversationId)
        {
            _cache.Remove(conversationId);
            _logger.LogDebug("Cache invalidated for conversation {ConversationId}", conversationId);
        }
    }

    public class CachedConversation
    {
        public readonly object LockObject = new();

        public Guid Id { get; set; }
        public ConversationType Type { get; set; }
        public string? Name { get; set; }
        public string? AvatarUrl { get; set; }
        public bool IsWriteRestricted { get; set; }
        public DateTime UpdatedAt { get; set; }

        public long? LastMessageId { get; set; }
        public string? LastMessageText { get; set; }
        public Guid? LastMessageSenderId { get; set; }
        public DateTime? LastMessageCreatedAt { get; set; }

        public HashSet<Guid> Members { get; set; } = new();
        public HashSet<Guid> Admins { get; set; } = new();
        public Dictionary<Guid, int> UnreadCounts { get; set; } = new();
        public Dictionary<Guid, long?> LastReadMessageIds { get; set; } = new();

        public bool IsMember(Guid userId) => Members.Contains(userId);
        public bool IsAdmin(Guid userId) => Admins.Contains(userId);
        public int GetUnreadCount(Guid userId) => UnreadCounts.GetValueOrDefault(userId);
        public long? GetLastReadMessageId(Guid userId) => LastReadMessageIds.GetValueOrDefault(userId);
        public void UpdateReadStatus(Guid userId, long messageId, int unreadCount = 0)
        {
            lock (LockObject)
            {
                UnreadCounts[userId] = unreadCount;
                LastReadMessageIds[userId] = messageId;
            }
        }
    }
}
