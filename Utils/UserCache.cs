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
            DateTime LastSeenAt
        );

        private readonly ConcurrentDictionary<Guid, string> _connections = new();
        private readonly ConcurrentDictionary<Guid, bool> _onlineStatus = new();
        private readonly ConcurrentDictionary<Guid, CachedUser> _userData = new();

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


        public void InitializeAllUsers(IEnumerable<CachedUser> users)
        {
            _userData.Clear();
            foreach (var user in users)
            {
                _userData[user.Id] = user;
            }
        }
        public void AddOrUpdateUser(Guid userId, string displayName, string? avatarUrl, string? customStatus, DateTime lasSeenAt)
        {
            _userData[userId] = new CachedUser(userId, displayName, avatarUrl, customStatus, lasSeenAt);
        }

        public CachedUser? GetUser(Guid userId)
        {
            return _userData.TryGetValue(userId, out var user) ? user : null;
        }

        public string GetDisplayName(Guid userId)
        {
            return _userData.TryGetValue(userId, out var user) ? user.DisplayName ?? "-" : "-";
        }
    }
}
