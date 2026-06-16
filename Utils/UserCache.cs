using System.Collections.Concurrent;

namespace IDMChat.Utils
{
    public class UserCache
    {
        private readonly ConcurrentDictionary<Guid, string> _connections = new();
        private readonly ConcurrentDictionary<Guid, bool> _onlineStatus = new();
        private readonly ConcurrentDictionary<Guid, string> _displayNames = new();

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

        public void AddOrUpdateUser(Guid userId, string displayName)
        {
            _displayNames[userId] = displayName;
        }

        public string? GetDisplayName(Guid userId)
        {
            return _displayNames.GetValueOrDefault(userId);
        }
    }
}
