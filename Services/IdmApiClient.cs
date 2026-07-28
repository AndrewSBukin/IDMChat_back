using IDMChat.DTO;

namespace IDMChat.Services
{
    public interface IIdmApiClient
    {
        Task<IdmAuthResultDto?> VerifyCredentialsAsync(string username, string password, CancellationToken ct = default);

    }


    public class IdmApiClient : IIdmApiClient
    {
        private readonly HttpClient _http;

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

    }
}
