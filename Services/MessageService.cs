using IDMChat.Domain;
using IDMChat.Models;
using System.ComponentModel.DataAnnotations;
using System.Threading.Channels;

namespace IDMChat.Services
{
    public class MessageService
    {
        // TODO: implement
        private static readonly Dictionary<int, User> _users = new();
        private static readonly Dictionary<int, Channel> _channels = new();
        private static int _nextMessageId = 1;

        // TODO: implement
        //public MessageService(IUserRepository userRepo, IChannelRepository channelRepo)
        //{
        //    _userRepo = userRepo;
        //    _channelRepo = channelRepo;
        //}

        public async Task<Message> SendMessageAsync(int userId, string text, int channelId)
        {
            // Валидация входных данных
            if (string.IsNullOrWhiteSpace(text))
                throw new ValidationException("Сообщение не может быть пустым");

            if (text.Length > 500)
                throw new ValidationException($"Сообщение не может превышать 500 символов (сейчас: {text.Length})");

            var user = await GetUserByIdAsync(userId);
            if (user == null)
                throw new NotFoundException("User", userId);

            if (user.IsMuted)
                throw new ForbiddenException($"Пользователь {userId} заблокирован до {user.MutedUntil}");

            var channel = await GetChannelByIdAsync(channelId);
            if (channel == null)
                throw new NotFoundException("Channel", channelId);

            if (!channel.CanWrite(user))
                throw new ForbiddenException($"Пользователь {userId} не имеет прав на запись в канал {channelId}");

            if (await IsRateLimitedAsync(userId))
                throw new RateLimitException(30);

            // Успех - создаем сообщение
            var message = new Message
            {
                Id = _nextMessageId++,
                UserId = userId,
                ChannelId = channelId,
                Text = text,
                CreatedAt = DateTime.UtcNow
            };

            await SaveMessageAsync(message);

            return message;
        }

        private Task<User?> GetUserByIdAsync(int id)
        => Task.FromResult(_users.GetValueOrDefault(id));

        private Task<Channel?> GetChannelByIdAsync(int id)
            => Task.FromResult(_channels.GetValueOrDefault(id));

        // TODO: implement
        private Task<bool> IsRateLimitedAsync(int userId)
            => Task.FromResult(false); // Заглушка

        // TODO: implement
        private Task SaveMessageAsync(Message message)
            => Task.CompletedTask; // Заглушка

        public class User
        {
            public int Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public bool IsMuted { get; set; }
            public DateTime MutedUntil { get; set; }
        }

        public class Channel
        {
            public int Id { get; set; }
            public string Name { get; set; } = string.Empty;

            // TODO: implement
            public bool CanWrite(User user) => true;
        }

        public class Message
        {
            public int Id { get; set; }
            public int UserId { get; set; }
            public int ChannelId { get; set; }
            public string Text { get; set; } = string.Empty;
            public DateTime CreatedAt { get; set; }
        }
    }
}
