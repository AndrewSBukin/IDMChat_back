using IDMChat.DTO;

namespace IDMChat.Services
{
    public interface IIdmApiClient
    {
        //// Пример вызова от имени системы (вытащить категории)
        //Task<List<CategoryDto>?> GetCategoriesAsync(CancellationToken ct = default);
        //// Пример вызова с передачей ID конкретного юзера
        //Task<UserPermissionsDto?> GetUserPermissionsAsync(int idmUserId, CancellationToken ct = default);

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

        //public async Task<List<CategoryDto>?> GetCategoriesAsync(CancellationToken ct = default)
        //{
        //    // Метод PostAsJsonAsync или GetAsync теперь пишется в 1 строчку
        //    var response = await _http.GetAsync("api/internal/categories", ct);
        //    response.EnsureSuccessStatusCode();

        //    return await response.Content.ReadFromJsonAsync<List<CategoryDto>>(cancellationToken: ct);
        //}

        //public async Task<UserPermissionsDto?> GetUserPermissionsAsync(int idmUserId, CancellationToken ct = default)
        //{
        //    var response = await _http.GetAsync($"api/internal/permissions?userId={idmUserId}", ct);
        //    if (!response.IsSuccessStatusCode) return null;

        //    return await response.Content.ReadFromJsonAsync<UserPermissionsDto>(cancellationToken: ct);
        //}

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
