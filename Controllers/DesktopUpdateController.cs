using IDMChat.DTO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace IDMChat.Controllers
{
    [AllowAnonymous]
    [ApiController]
    [Route("api/v1/desktop")]
    public class DesktopUpdateController : ControllerBase
    {
        private readonly ILogger<DesktopUpdateController> _logger;
        private readonly string _storagePath;

        public DesktopUpdateController(ILogger<DesktopUpdateController> logger, IConfiguration configuration, IWebHostEnvironment env)
        {
            _logger = logger;
            // Указываем путь к созданной папке E:\uploads\desktop-updates
            // Лучше всего прописать его в appsettings.json, либо использовать дефолтный путь IIS
            _storagePath = configuration.GetValue<string>("DesktopUpdates:StoragePath")
                           ?? @"C:\inetpub\wwwroot\chat_back\uploads\desktop-updates";
        }


        // ==========================================
        // 1. ЭНДПОИНТ ПРОВЕРКИ ОБНОВЛЕНИЙ
        // ==========================================
        [HttpGet("update")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DesktopUpdateResponse))]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> GetLatestVersion(
            [FromQuery] string platform,
            [FromQuery] string version,
            [FromQuery] int build,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(platform))
                return BadRequest(new { error = "Параметр platform обязателен" });

            string targetPlatform = platform.ToLower().Trim();
            if (targetPlatform != "windows" && targetPlatform != "macos")
                return BadRequest(new { error = "Поддерживаются только платформы windows или macos" });

            // Логируем старую версию для аналитики
            _logger.LogInformation("Запрос обновления: Платформа: {Platform}, Версия: {Version}, Сборка: {Build}",
                targetPlatform, version ?? "unknown", build);

            string fileName = $"update-{targetPlatform}.json";
            string filePath = Path.Combine(_storagePath, fileName);

            if (!System.IO.File.Exists(filePath))
            {
                _logger.LogWarning("Файл конфигурации обновления не найден: {FilePath}", filePath);
                return NoContent(); // 204 No Content, если релизов еще нет
            }

            try
            {
                string jsonContent = await System.IO.File.ReadAllTextAsync(filePath, ct);
                var updateData = JsonSerializer.Deserialize<DesktopUpdateResponse>(jsonContent, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (updateData == null)
                    return StatusCode(500, new { error = "Файл конфигурации пуст" });

                // Строгая валидация обязательных полей перед отправкой
                if (string.IsNullOrWhiteSpace(updateData.version) ||
                    updateData.build <= 0 ||
                    string.IsNullOrWhiteSpace(updateData.url) ||
                    !updateData.url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(updateData.sha256) ||
                    updateData.sha256.Length != 64)
                {
                    _logger.LogCritical("Файл {FileName} не прошёл строгую валидацию контракта!", fileName);
                    return StatusCode(500, new { error = "Конфигурация обновления невалидна" });
                }

                return Ok(updateData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при обработке файла конфигурации {FileName}", fileName);
                return StatusCode(500, new { error = "Внутренняя ошибка сервера обновлений" });
            }
        }

        // ==========================================
        // 2. РАЗДАЧА ФАЙЛОВ УСТАНОВЩИКОВ
        // ==========================================
        [HttpGet("download/{fileName}")]
        public IActionResult DownloadInstaller(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return BadRequest("Имя файла не указано");

            // Защита от Directory Traversal (чтобы умники не скачали файлы из других папок через ../)
            var safeFileName = Path.GetFileName(fileName);
            string filePath = Path.Combine(_storagePath, safeFileName);

            if (!System.IO.File.Exists(filePath))
                return NotFound("Установщик не найден");

            // Определяем тип контента
            string contentType = safeFileName.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase) ||
                                 safeFileName.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase)
                                 ? "application/x-apple-diskimage"
                                 : "application/octet-stream";

            // Отдаем файл с поддержкой докачки (Range), что гарантирует заголовок Content-Length
            return PhysicalFile(
                physicalPath: filePath,
                contentType: contentType,
                fileDownloadName: safeFileName,
                enableRangeProcessing: true
            );
        }
    }
}
