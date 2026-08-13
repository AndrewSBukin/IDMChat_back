using IDMChat.Models;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace IDMChat.Utils
{
    public class UserCache
    {
        public record CachedUser(
            Guid Id,
            string DisplayName,
            string? AvatarUrl,
            string? CustomStatus,
            DateTime LastSeenAt,
            int? IdmUserId,
            bool IsActive
        );

        private readonly ConcurrentDictionary<Guid, string> _connections = new();
        private readonly ConcurrentDictionary<Guid, bool> _onlineStatus = new();
        private readonly ConcurrentDictionary<Guid, CachedUser> _userData = new();
        private readonly ConcurrentDictionary<Guid, Guid> _activeChats = new();
        private readonly ConcurrentDictionary<int, Guid> _idmToGuidMap = new();

        public void AddConnection(Guid userId, string connectionId)
        {
            _connections.AddOrUpdate(userId, connectionId, (_, _) => connectionId);
            _onlineStatus[userId] = true;
        }

        public void RemoveConnection(Guid userId, string connectionId)
        {
            if (_connections.TryGetValue(userId, out var current) && current == connectionId)
                _connections.TryRemove(userId, out _);

            if (!_connections.ContainsKey(userId))
                _onlineStatus.TryRemove(userId, out _);
        }

        public string? GetConnectionId(Guid userId)
        {
            return _connections.TryGetValue(userId, out var connectionId) ? connectionId : null;
        }

        public bool IsOnline(Guid userId) => _connections.ContainsKey(userId);

        public List<Guid> GetOnlineMembers(HashSet<Guid> userIds)
        {
            return userIds.Where(IsOnline).ToList();
        }
        public List<Guid> GetOnlineMembers()
        {
            return _connections.Keys.ToList();
        }


        public void InitializeAllUsers(IEnumerable<CachedUser> users)
        {
            _userData.Clear();
            foreach (var user in users)
            {
                _userData[user.Id] = user;
                if (user.IdmUserId.HasValue)
                {
                    _idmToGuidMap.TryAdd(user.IdmUserId.Value, user.Id);
                }
            }
        }
        public void AddOrUpdateUser(Guid userId, string displayName, string? avatarUrl, string? customStatus, DateTime lasSeenAt, int? idmUserId = null, bool isActive = true)
        {
            _userData[userId] = new CachedUser(userId, displayName + (isActive ? "" : " [Блокирован]"), avatarUrl, customStatus, lasSeenAt, idmUserId, isActive);
            if (idmUserId.HasValue)
            {
                _idmToGuidMap[idmUserId.Value] = userId;
            }
        }

        public CachedUser? GetUser(Guid userId)
        {
            return _userData.TryGetValue(userId, out var user) ? user : null;
        }

        public Guid? GetChatUserIdByIdmId(int idmUserId)
        {
            if (_idmToGuidMap.TryGetValue(idmUserId, out var guid))
            {
                return guid;
            }
            return null;
        }

        public string GetDisplayName(Guid userId)
        {
            return _userData.TryGetValue(userId, out var user) ? user.DisplayName ?? "-" : "-";
        }


        public void JoinConversation(Guid userId, Guid conversationId)
        {
            _activeChats[userId] = conversationId;
        }

        public void LeaveConversation(Guid userId)
        {
            _activeChats.TryRemove(userId, out _);
        }
        public Guid? GetCurrentChatId(Guid userId)
        {
            if (_activeChats.TryGetValue(userId, out var conversationId))
            {
                return conversationId;
            }
            return null;
        }
    }
}
