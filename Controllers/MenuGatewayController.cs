using Asp.Versioning;
using IDMChat.Middleware;
using IDMChat.Models;
using IDMChat.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IDMChat.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/v{version:apiVersion}/menugateway")]
    [ApiVersion("1.0")]
    public class MenuGatewayController : ControllerBase
    {
        private readonly ChatDbContext _db;
        private readonly HttpClient _httpClient;
        private readonly IAuthContextService _authContextService;

        public MenuGatewayController(ChatDbContext db, HttpClient httpClient, IAuthContextService authContextService)
        {
            _db = db;
            _httpClient = httpClient;
            _authContextService = authContextService;
        }


        /// <summary>
        /// Универсальный Catch-All шлюз для любых отчетов, диапазонов дат и POST-команд (создание расходов и т.д.)
        /// Примеры вызовов от фронтенда:
        /// GET  /api/v1/menugateway/club.daily/GetDailyReport?pointID=149&date=09-02-2026
        /// GET  /api/v1/menugateway/club.history/GetRangeReport?pointID=149&start=01-01-2026&end=05-01-2026
        /// POST /api/v1/menugateway/daily.expense.edit/CreateExpense (с тяжелым JSON в теле)
        /// </summary>
        [AcceptVerbs("GET", "POST", "PUT", "DELETE")]
        [Route("{screenKey}/{*action}")] // Ловит любые HTTP-методы: GET, POST, PUT, DELETE
        public async Task<IActionResult> ProxyRequest234(string screenKey, string action)
        {
            var userId = HttpContext.GetCurrentUserId();

            // Здесь мы проверяем, имеет ли пользователь в принципе доступ к этому экрану/праву.
            // Если у пользователя нет screenKey ("club.daily" или "daily.expense.edit") в его меню/правах — жесткий отказ.
            var authContext = await _authContextService.GetContextAsync(userId, HttpContext.RequestAborted);
            if (authContext == null) return Forbid();

            bool hasPermission = authContext.permissions.Contains(screenKey);
            bool hasMenuAccess = authContext.menu.Any(m => m.key == screenKey || m.children.Any(c => c.key == screenKey));

            // Если доступа нет ни в правах, ни в меню — жестко блокируем запрос к ИДМ
            if (!hasPermission && !hasMenuAccess)
            {
                return Forbid();
            }

            var user = await _db.Users.FindAsync(new object[] { userId });
            if (user == null || !user.IdmUserId.HasValue)
            {
                return Unauthorized("Пользователь не сопоставлен с ИДМ");
            }

            // 2. СТРОИМ ПУТЬ К ИДМ
            // Извлекаем оригинальную строку запроса (QueryString), например: ?pointID=149&date=09-02-2026
            var queryString = HttpContext.Request.QueryString.Value;

            // Мапим запрос на внутренний контроллер ИДМ, выделенный под чат (например, /api/chatgateway/)
            var idmTargetUrl = $"{_httpClient.BaseAddress}chatgateway/{action}{queryString}";

            // 3. ФОРМИРУЕМ ТРАНЗИТНЫЙ ЗАПРОС К ИДМ
            var targetMethod = new HttpMethod(HttpContext.Request.Method);
            using var outboundRequest = new HttpRequestMessage(targetMethod, idmTargetUrl);

            // Переносим тело запроса (если это POST/PUT с тяжелым JSON-объектом приходов/расходов)
            if (HttpContext.Request.ContentLength > 0)
            {
                // Стримим тело запроса от клиента напрямую в ИДМ, не загружая его целиком в память чата
                outboundRequest.Content = new StreamContent(HttpContext.Request.Body);
                outboundRequest.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue(HttpContext.Request.ContentType ?? "application/json");
            }

            // Добавляем наши обязательные заголовки: секретный ключ и ID инициатора в ИДМ для внутренних проверок
            outboundRequest.Headers.Add("X-Internal-Api-Key", "SuperSecretKey_IdmToChat_2026_SecureToken!");
            outboundRequest.Headers.Add("X-Chat-Initiator-IdmId", user.idm); // Передаем родной ID пользователя в ИДМ

            // 4.ОТПРАВЛЯЕМ В ИДМ И СТРИМИМ ОТВЕТ НАЗАД КЛИЕНТУ
            var response = await _httpClient.SendAsync(outboundRequest, HttpCompletionOption.ResponseHeadersRead);

            if (!response.IsSuccessStatusCode)
            {
                return StatusCode((int)response.StatusCode, "ИДМ отказала в обработке операции");
            }

            var responseStream = await response.Content.ReadAsStreamAsync();
            return File(responseStream, response.Content.Headers.ContentType?.ToString() ?? "application/json");
        }
    }
}
