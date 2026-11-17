using IDMChat.DTO;

namespace IDMChat.Services
{
    public interface IIdmApiClient
    {
        HttpClient Http { get; }
        Task<List<IdmClubDto>> GetUserClubsAsync(int userId, CancellationToken ct);
        Task<IdmAuthResultDto?> VerifyCredentialsAsync(string username, string password, CancellationToken ct = default);

        /// <summary>
        /// Отправляет в ИДМ событие bot-команды (текстовая команда "/..." или нажатие кнопки).
        /// Контракт: POST /api/chatbot/command с телом BotCommandRequestDto.
        /// </summary>
        Task<bool> SendBotCommandAsync(BotCommandRequestDto request, CancellationToken ct = default);

    }


    public class IdmApiClient : IIdmApiClient
    {
        private readonly HttpClient _http;
        public HttpClient Http => _http;

        // Внедряем HttpClient. Фабрика настроит его автоматически!
        public IdmApiClient(HttpClient http)
        {
            _http = http;
        }

        public async Task<IdmAuthResultDto?> VerifyCredentialsAsync(string username, string password, CancellationToken ct = default)
        {
            try
            {
                var response = await _http.PostAsJsonAsync("/auth/verify", new { username, password }, ct);

                // Если ИДМ вернула 401/400 (неверный пароль или заблокирован), возвращаем null
                if (!response.IsSuccessStatusCode) return null;

                return await response.Content.ReadFromJsonAsync<IdmAuthResultDto>(cancellationToken: ct);
            }
            catch
            {
                return null;
            }
        }

        public async Task<List<IdmClubDto>> GetUserClubsAsync(int userId, CancellationToken ct = default)
        {
            try
            {
                // Отправляем точечный POST-запрос с ID пользователя в теле, согласно стилю ИДМ
                var response = await _http.PostAsJsonAsync("/api/userclubs", new { userId }, ct);

                // Если ИДМ вернула ошибку (нет прав, пользователь не найден или сервер упал), возвращаем пустой список
                if (!response.IsSuccessStatusCode) return null;

                return await response.Content.ReadFromJsonAsync<List<IdmClubDto>>(cancellationToken: ct);
            }
            catch
            {
                // При таймауте или сетевом сбое возвращаем пустой список (безопасный фолбэк)
                return null;
            }
        }

        /// <summary>
        /// Контракт для ИДМ: POST /api/chatbot/command с телом BotCommandRequestDto (snake_case).
        /// В заголовках HttpClient уже проставлен X-Internal-Api-Key.
        /// </summary>
        public async Task<bool> SendBotCommandAsync(BotCommandRequestDto request, CancellationToken ct = default)
        {
            try
            {
                var response = await _http.PostAsJsonAsync($"{Http.BaseAddress}chatgateway/chatbot/command", request, ct);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                // Сетевой сбой не должен ронять обработчик команд - ИДМ разберётся по своим логам
                return false;
            }
        }
    }
}
