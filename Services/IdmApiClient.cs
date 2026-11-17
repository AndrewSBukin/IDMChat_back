using IDMChat.DTO;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

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
        private readonly ILogger<IdmApiClient> _logger;
        public HttpClient Http => _http;

        // Внедряем HttpClient. Фабрика настроит его автоматически!
        public IdmApiClient(HttpClient http, ILogger<IdmApiClient> logger)
        {
            _http = http;
            _logger = logger;
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
            var url = $"{Http.BaseAddress}chatgateway/chatbot/command";
            _logger.LogDebug("my-debug bot-command HTTP to IDM: url={Url} dto={@Dto}", url, request);
            var sw = Stopwatch.StartNew();
            try
            {
                // ИДМ опознаёт отправителя по заголовку X-Chat-Initiator-IdmId (id сотрудника в ИДМ).
                // PostAsJsonAsync не умеет per-request заголовки, поэтому собираем запрос вручную.
                var requestMsg = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create(request)
                };
                if (request.user_idm_id.HasValue)
                    requestMsg.Headers.Add("X-Chat-Initiator-IdmId", request.user_idm_id.Value.ToString());

                var response = await _http.SendAsync(requestMsg, ct);
                var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
                _logger.LogDebug("my-debug bot-command HTTP from IDM: status={Status} body={Body} elapsed={ElapsedMs}ms",
                    (int)response.StatusCode, body, sw.ElapsedMilliseconds);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                // Сетевой сбой не должен ронять обработчик команд - ИДМ разберётся по своим логам
                _logger.LogError(ex, "my-debug bot-command HTTP to IDM failed: url={Url} elapsed={ElapsedMs}ms", url, sw.ElapsedMilliseconds);
                return false;
            }
        }
    }
}
