using Asp.Versioning;
using FFMpegCore;
using FFMpegCore.Extend;
using IDMChat.DTO;
using IDMChat.Middleware;
using IDMChat.Models;
using IDMChat.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using static System.Net.WebRequestMethods;

namespace IDMChat.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Route("api/v{version:apiVersion}/[controller]")]
    [Authorize]
    [ApiVersion("1.0")]
    public class FilesController : ControllerBase
    {
        private readonly IWebHostEnvironment _environment;
        private readonly ILogger<FilesController> _logger;
        private readonly ChatDbContext _db;
        private readonly IChatPathUrlResolver _urlResolver;
        private const long MaxFileSize = 100 * 1024 * 1024; // 100MB

        private static readonly HashSet<string> AllowedImageTypes = new()
        {
            "image/jpeg", "image/png", "image/gif", "image/webp", "image/heic"
        };

        private static readonly HashSet<string> AllowedVideoTypes = new()
        {
            "video/mp4", "video/mpeg", "video/quicktime", "video/webm"
        };

        private static readonly HashSet<string> AllowedFileTypes = new()
        {
            "application/pdf",
            "application/msword",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "application/vnd.ms-excel",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "application/zip", "application/x-rar-compressed",
            "text/plain"
        };
        private readonly string _storageBasePath;

        private string GetMimeType(string fileName)
        {
            var extension = Path.GetExtension(fileName).ToLower();
            return extension switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".mp4" => "video/mp4",
                ".pdf" => "application/pdf",
                _ => "application/octet-stream"
            };
        }

        public FilesController(ChatDbContext dbContext, IWebHostEnvironment environment, ILogger<FilesController> logger, IConfiguration configuration, IChatPathUrlResolver urlResolver)
        {
            _db = dbContext;
            _environment = environment;
            _logger = logger;
            _storageBasePath = configuration["Storage:BasePath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "uploads");
            _urlResolver = urlResolver;
        }

        [HttpPost("upload")]
        public async Task<ActionResult<UploadFileResponse>> UploadFile(IFormFile file, [FromForm][Required] string type, [FromForm] Guid? conversationId = null, [FromForm] string? waveform = null, CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();

            // 1. Проверка типа
            if (!Enum.TryParse<FileType>(type, true, out var fileType))
                return UnprocessableEntity(new { error = new { code = "INVALID_FORMAT", message = $"Неверный тип: {type}" } });

            // 2. Проверка наличия файла
            if (file == null || file.Length == 0)
                return BadRequest(new { error = new { code = "FILE_REQUIRED", message = "Файл обязателен" } }); 

            // 3. Проверка размера (100MB)
            const long maxFileSize = 100 * 1024 * 1024;
            if (file.Length > maxFileSize)
                return UnprocessableEntity(new { error = new { code = "FILE_TOO_LARGE", message = "Файл превышает 100MB" } });

            // --- ВАЛИДАЦИЯ WAVEFORM 
            string? validatedWaveformJson = null;
            List<double>? validatedWaveformList = null;
            if (!string.IsNullOrWhiteSpace(waveform))
            {
                try
                {
                    // Пытаемся распарсить строку в JSON-массив чисел
                    var rawList = JsonSerializer.Deserialize<List<double>>(waveform);

                    // Проверяем условия из ТЗ: длина <= 64 и значения строго в диапазоне [0, 1]
                    if (rawList != null && rawList.Count <= 64 && rawList.All(v => v >= 0.0 && v <= 1.0))
                    {
                        validatedWaveformList = rawList;
                        // Сохраняем в максимально компактную строку для базы данных
                        validatedWaveformJson = JsonSerializer.Serialize(rawList);
                    }
                }
                catch (JsonException)
                {
                    // Если прилетел мусор или некорректный формат — по ТЗ мягко игнорируем, не падая
                }
            }

            var fileId = Guid.NewGuid();
            var originalExtension = Path.GetExtension(file.FileName);
            var fileName = $"{fileId}{originalExtension}";
            var thumbFileName = $"{fileId}_thumb.jpg";

            var datePath = DateTime.UtcNow.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
            var uploadsDir = Path.Combine(_storageBasePath, "files", datePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(uploadsDir);

            var filePath = Path.Combine(uploadsDir, fileName);
            var thumbPath = Path.Combine(uploadsDir, thumbFileName);

            // Просто сохраняем файл (без конвертации)
            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream, ct);
            }

            // 6. Генерируем миниатюру (только для изображений)
            bool hasThumbnail = false;
            int? duration = null;
            // Генерация миниатюры через FFmpeg для изображений
            if (fileType == FileType.Image)
            {
                await GenerateImageThumbnailWithFfmpeg(filePath, 200, 200);
                hasThumbnail = true;
            }
            else if (fileType == FileType.Video)
            {
                var mediaInfo = await FFProbe.AnalyseAsync(filePath);
                duration = (int)mediaInfo.Duration.TotalSeconds;
                int ts = 5;

                if (duration < 10)
                    ts = (int)duration / 2;
                await GenerateVideoThumbnailAsync(filePath, thumbPath, TimeSpan.FromSeconds(ts));
                hasThumbnail = true;
            }

            var storageRelativePath = $"files/{datePath}/{fileName}";
            var thumbnailRelativePath = hasThumbnail ? $"files/{datePath}/{thumbFileName}" : null;

            // Сохраняем в БД
            var attachment = new FileAttachment
            {
                Id = fileId,
                UserId = userId,
                ConversationId = conversationId ?? Guid.Empty,
                FileName = file.FileName,
                FileSize = file.Length,
                MimeType = file.ContentType,
                StoragePath = storageRelativePath,
                ThumbnailPath = thumbnailRelativePath,
                Type = fileType,
                Duration = duration,
                CreatedAt = DateTime.UtcNow,
                WaveformJson = validatedWaveformJson
            };

            if (fileType == FileType.Voice)
            {
                // Получить длительность через FFprobe (или другой способ)
                attachment.Duration = await GetAudioDuration(filePath);
            }

            _db.FileAttachments.Add(attachment);
            await _db.SaveChangesAsync(ct);

            return Ok(new UploadFileResponse
            {
                Id = fileId,
                FileName = file.FileName,
                FileSize = file.Length,
                MimeType = file.ContentType,
                Url = _urlResolver.ResolveUrl(storageRelativePath),
                ThumbnailUrl = _urlResolver.ResolveUrl(thumbnailRelativePath),
                Duration = duration,
                waveform = validatedWaveformList
            });
        }

        private FileType DetermineFileType(IFormFile file)
        {
            // 1. Пытаемся взять MIME-тип, который прислал браузер клиента
            string mimeType = file.ContentType?.ToLowerInvariant() ?? string.Empty;

            // 2. Если браузер прислал дефолтный "application/octet-stream" или пустоту, 
            // определяем MIME-тип самостоятельно по расширению файла
            if (string.IsNullOrEmpty(mimeType) || mimeType == "application/octet-stream")
            {
                var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
                if (provider.TryGetContentType(file.FileName, out var detectedMimeType))
                {
                    mimeType = detectedMimeType.ToLowerInvariant();
                }
            }

            // 3. Маппим MIME-тип на ваше внутреннее перечисление FileType
            if (mimeType.StartsWith("image/"))
            {
                return FileType.Image;
            }

            if (mimeType.StartsWith("video/"))
            {
                return FileType.Video;
            }

            if (mimeType.StartsWith("audio/"))
            {
                // Нюанс: Как отличить аудиофайл от голосового (Voice)?
                // В мессенджерах голосовые обычно записываются в форматах .opus, .ogg, .aac, .m4a
                // Самый надежный маркер в рамках вашего ТЗ — если у файла специфическое расширение, 
                // либо если в запросе для этого файла передан waveform.
                // Пока для базовой логики вернем Voice, если расширение типично для диктофона, либо оставьте ваш тип Audio.
                var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                if (ext == ".opus" || ext == ".m4a" || ext == ".aac" || mimeType.Contains("opus"))
                {
                    return FileType.Voice;
                }

                return FileType.Voice; // Подставьте ваше перечисление, например FileType.Audio или FileType.Voice
            }

            // 4. Для всех остальных типов документов (pdf, docx, zip, txt) возвращаем общий тип
            return FileType.File;
        }

        [HttpPost("upload-multiple")] // Меняем роут, чтобы не ломать старый метод, либо заменяем его
        public async Task<ActionResult<UploadMultipleFilesResponse>> UploadMultipleFiles(
            List<IFormFile> files, // Принимаем коллекцию файлов
            [FromForm] Guid? conversationId = null,
            [FromForm] List<string>? waveforms = null, // Массив строк-JSON вевйвформов в порядке файлов
            CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();
            var response = new UploadMultipleFilesResponse();

            // 2. Проверка наличия файлов
            if (files == null || files.Count == 0)
                return BadRequest(new { error = new { code = "FILES_REQUIRED", message = "Не прикреплено ни одного файла" } });

            // 3. Быстрая проверка лимитов до начала записи на диск (например, суммарный размер не более 300MB)
            const long maxSingleFileSize = 100 * 1024 * 1024;
            if (files.Any(f => f.Length > maxSingleFileSize))
                return UnprocessableEntity(new { error = new { code = "FILE_TOO_LARGE", message = "Один или несколько файлов превышают 100MB" } });

            var datePath = DateTime.UtcNow.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
            var uploadsDir = Path.Combine(_storageBasePath, "files", datePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(uploadsDir);

            var attachmentsToAdd = new List<FileAttachment>();

            // 4. Поочередно обрабатываем каждый файл в цикле
            for (int i = 0; i < files.Count; i++)
            {
                var file = files[i];
                if (file.Length == 0) continue;

                FileType fileType = DetermineFileType(file);

                // --- ВАЛИДАЦИЯ WAVEFORM ДЛЯ КОНКРЕТНОГО ФАЙЛА ---
                string? validatedWaveformJson = null;
                List<double>? validatedWaveformList = null;

                // Достаем вейвформ, соответствующий текущему файлу по индексу
                string? rawWaveform = waveforms != null && i < waveforms.Count ? waveforms[i] : null;

                if (!string.IsNullOrWhiteSpace(rawWaveform))
                {
                    try
                    {
                        var rawList = JsonSerializer.Deserialize<List<double>>(rawWaveform);
                        if (rawList != null && rawList.Count <= 64 && rawList.All(v => v >= 0.0 && v <= 1.0))
                        {
                            validatedWaveformList = rawList;
                            validatedWaveformJson = JsonSerializer.Serialize(rawList);
                        }
                    }
                    catch (JsonException) { /* Игнорируем мусор по ТЗ */ }
                }

                // --- ПОДГОТОВКА ПУТЕЙ И ИМЕН ---
                var fileId = Guid.NewGuid();
                var originalExtension = Path.GetExtension(file.FileName);
                var fileName = $"{fileId}{originalExtension}";
                var thumbFileName = $"{fileId}_thumb.jpg";

                var filePath = Path.Combine(uploadsDir, fileName);
                var thumbPath = Path.Combine(uploadsDir, thumbFileName);

                // Сохраняем физический файл на диск
                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream, ct);
                }

                // --- ГЕНЕРАЦИЯ МИНИАТЮР / МЕТАДАННЫХ ---
                bool hasThumbnail = false;
                int? duration = null;

                if (fileType == FileType.Image)
                {
                    await GenerateImageThumbnailWithFfmpeg(filePath, 200, 200);
                    hasThumbnail = true;
                }
                else if (fileType == FileType.Video)
                {
                    var mediaInfo = await FFProbe.AnalyseAsync(filePath);
                    duration = (int)mediaInfo.Duration.TotalSeconds;
                    int ts = duration < 10 ? (int)duration / 2 : 5;

                    await GenerateVideoThumbnailAsync(filePath, thumbPath, TimeSpan.FromSeconds(ts));
                    hasThumbnail = true;
                }
                else if (fileType == FileType.Voice)
                {
                    duration = await GetAudioDuration(filePath);
                }

                var storageRelativePath = $"files/{datePath}/{fileName}";
                var thumbnailRelativePath = hasThumbnail ? $"files/{datePath}/{thumbFileName}" : null;

                // Создаем сущность для БД, но пока НЕ сохраняем
                var attachment = new FileAttachment
                {
                    Id = fileId,
                    UserId = userId,
                    ConversationId = conversationId ?? Guid.Empty,
                    FileName = file.FileName,
                    FileSize = file.Length,
                    MimeType = file.ContentType,
                    StoragePath = storageRelativePath,
                    ThumbnailPath = thumbnailRelativePath,
                    Type = fileType,
                    Duration = duration,
                    CreatedAt = DateTime.UtcNow,
                    WaveformJson = validatedWaveformJson
                };

                attachmentsToAdd.Add(attachment);

                // Добавляем элемент в общий DTO-ответ
                response.files.Add(new UploadFileResponse
                {
                    Id = fileId,
                    FileName = file.FileName,
                    FileSize = file.Length,
                    MimeType = file.ContentType,
                    Url = _urlResolver.ResolveUrl(storageRelativePath),
                    ThumbnailUrl = _urlResolver.ResolveUrl(thumbnailRelativePath),
                    Duration = duration,
                    waveform = validatedWaveformList
                });
            }

            // 5. Сохраняем ВСЕ записи вложений в базу данных одним махом
            if (attachmentsToAdd.Any())
            {
                _db.FileAttachments.AddRange(attachmentsToAdd);
                await _db.SaveChangesAsync(ct);
            }

            return Ok(response);
        }
        private string? ValidateAndSerializeWaveform(string? rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson)) return null;

            try
            {
                // Пытаемся распарсить строку в список чисел
                var list = JsonSerializer.Deserialize<List<double>>(rawJson);

                if (list == null) return null;

                // Валидация требований: длина <= 64, все значения строго в диапазоне [0, 1]
                if (list.Count > 64) return null;
                if (list.Any(val => val < 0.0 || val > 1.0)) return null;

                // Если валидация пройдена, сериализуем обратно в компактную строку для БД
                return JsonSerializer.Serialize(list);
            }
            catch (JsonException)
            {
                // Если прилетел невалидный JSON или мусор — мягко игнорируем по ТЗ
                return null;
            }
        }

        private async Task<int> GetAudioDuration(string filePath)
        {
            var mediaInfo = await FFProbe.AnalyseAsync(filePath);
            return (int)Math.Ceiling(mediaInfo.Duration.TotalSeconds);
        }

        public static async Task GenerateImageThumbnailWithFfmpeg(string inputPath, int width, int height)
        {
            var originalExtension = Path.GetExtension(inputPath);
            var originalFilemane = Path.GetFileNameWithoutExtension(inputPath);
            var originalPath = Path.GetDirectoryName(inputPath);
            var thumbFileName = $"{originalFilemane}_thumb.jpg";
            var thumbPath = Path.Combine(originalPath, thumbFileName);
            await FFMpegArguments
                .FromFileInput(inputPath)
                .OutputToFile(thumbPath, true, options => options
                    .WithVideoFilters(filterOptions => filterOptions
                        .Scale(width, height))
                    .WithFrameOutputCount(1)
                    .ForceFormat("mjpeg"))
                .ProcessAsynchronously();
        }
        private async Task GenerateVideoThumbnailAsync(string inputPath, string outputPath, TimeSpan captureTime)
        {
            await FFMpegArguments
                .FromFileInput(inputPath)
                .OutputToFile(outputPath, false, options => options
                    .Seek(captureTime)
                    .WithFrameOutputCount(1)
                    .WithVideoFilters(filterOptions => filterOptions.Scale(320, 240))
                    .ForceFormat("mjpeg"))
                .ProcessAsynchronously();
        }

        [HttpGet("{**filePath}")]
        [AllowAnonymous]
        public async Task<IActionResult> GetFile(string filePath, [FromQuery] string? token, CancellationToken ct = default)
        {
            //var userId = HttpContext.GetCurrentUserId();

            var rawPath = Uri.UnescapeDataString(filePath).Replace('\\', '/');
            string relativePath = rawPath;

            if (rawPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                rawPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(rawPath);
                relativePath = uri.AbsolutePath; // Всегда вернет чистый путь от корня: "/api/files/files/2026/..."
            }

            relativePath = relativePath.Trim('/');
            var prefix = "api/files/";
            if (relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                relativePath = relativePath.Substring(prefix.Length);
            }

            var decodedPath = relativePath.Trim('/');

            // 1. Декодируем путь
            //var decodedPath = Uri.UnescapeDataString(filePath);
            var fullPath = Path.Combine(_storageBasePath, decodedPath);

            // 2. Проверяем существование файла
            if (!System.IO.File.Exists(fullPath))
                return NotFound(new { error = new { code = "FILE_NOT_FOUND", message = "Файл не найден" } });

            // 3. Проверка доступа в зависимости от типа пути
            var isAuthorized = true;
            var log = "decodedPath:"+ decodedPath+";";
            //if (decodedPath.StartsWith("files/"))
            //{
            //    // Файлы чата — проверяем через БД
            //    var attachment = await _db.FileAttachments.FirstOrDefaultAsync(f => f.StoragePath == decodedPath || f.ThumbnailPath == decodedPath, ct);

            //    if (attachment != null)
            //    {
            //        if (attachment.ConversationId == null || attachment.ConversationId == Guid.Empty)
            //        {
            //            log += "attachment without conversationid;";
            //            isAuthorized = (attachment.UserId == userId);
            //        }
            //        else
            //            isAuthorized = await _db.ConversationMembers.AnyAsync(cm => (cm.ConversationId == attachment.ConversationId) && cm.UserId == userId, ct);
            //    }
            //    else
            //        log += "attachment not found;";
            //}
            //else if (decodedPath.StartsWith("avatars/"))
            //{
            //    // Аватар доступен всем авторизованным
            //    isAuthorized = true;
            //}

            if (!isAuthorized)
                return StatusCode(403, new { error = new { code = "FORBIDDEN", message = "Доступ запрещён", log } });


            // 4. Отдаём файл
            var stream = System.IO.File.OpenRead(fullPath);
            var mimeType = GetMimeType(fullPath);

            return File(stream, mimeType);
        }

        public enum FileType
        {
            Image,
            Video,
            File,
            Voice
        }
    }
}
