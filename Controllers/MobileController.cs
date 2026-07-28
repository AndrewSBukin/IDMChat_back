using IDMChat.DTO;
using IDMChat.Middleware;
using IDMChat.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CorporateChat.Controllers.API.V1;

[Authorize] // 🔐 Доступ только для авторизованных сотрудников по JWT
[ApiController]
[Route("api/v1/[controller]")]
public class MobileAuthController : ControllerBase
{
    private readonly IAuthContextService _authContextService;

    public MobileAuthController(IAuthContextService authContextService)
    {
        _authContextService = authContextService;
    }

    /// <summary>
    /// Получение семантического контекста, иерархического меню, прав и лимитов для мобильного ЛК
    /// </summary>
    /// <param name="platform">Опциональный параметр для кастомной фильтрации (например, platform=mobile)</param>
    [HttpGet("bootstrap")]
    public async Task<ActionResult<AuthContextResponse>> GetBootstrapContext(
        [FromQuery] string? platform = null,
        CancellationToken ct = default)
    {
        // 1. Извлекаем UserId из текущего токена авторизации (используем ваш рабочий метод расширения)
        var userId = HttpContext.GetCurrentUserId();

        // 2. Запрашиваем собранный контракт. 
        // Сервис мгновенно заберет его из In-Memory кэша, либо (при промахе) соберет из БД за один проход.
        var context = await _authContextService.GetContextAsync(userId, ct);

        // 3. Точка кастомного переупорядочивания (Реализация ТЗ по параметру platform)
        if (platform == "mobile")
        {
            // Здесь при необходимости можно делать no-code корректировки для смартфонов, 
            // не затрагивая общую структуру БД. Например:
            // context.Menu.RemoveAll(m => m.Key == "only.web.feature");
        }

        // 4. Отдаем чистый, сериализованный в camelCase/snake_case JSON-ответ
        return Ok(context);
    }
}
