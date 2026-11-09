using IDMChat.Domain;
using Microsoft.Extensions.Caching.Memory;

namespace IDMChat.Services
{
    /// <summary>
    /// Простой rate-limiter на базе IMemoryCache (окно фиксированного размера).
    /// Без внешних зависимостей, потокобезопасен. Счётчики живут в памяти процесса
    /// и сбрасываются при перезапуске приложения - для одного сервера этого достаточно.
    /// При превышении лимита бросает RateLimitException, которую глобальный middleware
    /// превращает в HTTP 429 с заголовком Retry-After.
    /// </summary>
    public class LoginRateLimiter
    {
        private readonly IMemoryCache _cache;
        private readonly int _maxAttemptsPerUser;
        private readonly int _maxAttemptsPerIp;
        private readonly TimeSpan _window;

        public LoginRateLimiter(IMemoryCache cache, IConfiguration configuration)
        {
            _cache = cache;
            _maxAttemptsPerUser = configuration.GetValue("RateLimiting:Login:MaxAttemptsPerUser", 10);
            _maxAttemptsPerIp = configuration.GetValue("RateLimiting:Login:MaxAttemptsPerIp", 30);
            _window = TimeSpan.FromSeconds(configuration.GetValue("RateLimiting:Login:WindowSeconds", 300));
        }

        /// <summary>Лимит на конкретную пару IP + логин (защита от перебора конкретного аккаунта).</summary>
        public void CheckAndCountPerUser(string key) => CheckAndCountAttempt(key, _maxAttemptsPerUser);

        /// <summary>Общий лимит на IP (защита от распределённых атак с одного адреса).</summary>
        public void CheckAndCountPerIp(string key) => CheckAndCountAttempt(key, _maxAttemptsPerIp);

        /// <summary>Сбрасывает счётчик (вызывается после успешного входа).</summary>
        public void Reset(string key) => _cache.Remove(key);

        private void CheckAndCountAttempt(string key, int maxAttempts)
        {
            var counter = _cache.GetOrCreate(key, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = _window;
                return new Counter();
            })!;

            lock (counter)
            {
                var now = DateTime.UtcNow;

                // Окно истекло - начинаем новый отсчёт
                if (now - counter.WindowStart > _window)
                {
                    counter.WindowStart = now;
                    counter.Count = 0;
                }

                counter.Count++;

                if (counter.Count > maxAttempts)
                {
                    var retryAfterSeconds = Math.Max(1, (int)(_window - (now - counter.WindowStart)).TotalSeconds);
                    throw new RateLimitException(retryAfterSeconds);
                }
            }
        }

        private class Counter
        {
            public DateTime WindowStart = DateTime.UtcNow;
            public int Count;
        }
    }
}
