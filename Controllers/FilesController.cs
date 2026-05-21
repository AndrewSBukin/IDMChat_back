using Asp.Versioning;
using FFMpegCore;
using IDMChat.Middleware;
using IDMChat.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using FFMpegCore.Extend;

namespace IDMChat.Controllers
{
    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    [Authorize]
    [ApiVersion("1.0")]
    public class FilesController : ControllerBase
    {
        private readonly IWebHostEnvironment _environment;
        private readonly ILogger<FilesController> _logger;
        private readonly ChatDbContext _db;
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

        public FilesController(ChatDbContext dbContext, IWebHostEnvironment environment, ILogger<FilesController> logger, IConfiguration configuration)
        {
            _db = dbContext;
            _environment = environment;
            _logger = logger;
            _storageBasePath = configuration["Storage:BasePath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "uploads");
        }

        [HttpPost("upload")]
        public async Task<ActionResult<UploadFileResponse>> UploadFile(IFormFile file, [FromForm][Required] string type, [FromForm] Guid? conversationId = null, [FromForm] bool isAvatar = false, CancellationToken ct = default)
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

            var fileId = Guid.NewGuid();
            var originalExtension = Path.GetExtension(file.FileName);
            var fileName = $"{fileId}{originalExtension}";
            var thumbFileName = $"{fileId}_thumb.jpg";

            var datePath = DateTime.UtcNow.ToString("yyyy/MM/dd");
            var uploadsDir = Path.Combine(_storageBasePath, "files", datePath);
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
                await GenerateImageThumbnailWithFfmpeg(filePath, thumbPath, 200, 200);
                hasThumbnail = true;
            }
            else if (fileType == FileType.Video)
            {
                var mediaInfo = await FFProbe.AnalyseAsync(filePath);
                duration = (int)mediaInfo.Duration.TotalSeconds;

                await GenerateVideoThumbnailAsync(filePath, thumbPath, TimeSpan.FromSeconds(5));
                hasThumbnail = true;
            }

            // Сохраняем в БД
            var attachment = new FileAttachment
            {
                Id = fileId,
                UserId = userId,
                ConversationId = conversationId ?? Guid.Empty,
                FileName = file.FileName,
                FileSize = file.Length,
                MimeType = file.ContentType,
                StoragePath = Path.Combine("files", datePath, fileName),
                ThumbnailPath = hasThumbnail ? Path.Combine("files", datePath, thumbFileName) : null,
                Type = fileType,
                Duration = duration,
                CreatedAt = DateTime.UtcNow
            };

            if (fileType == FileType.Voice)
            {
                // Получить длительность через FFprobe (или другой способ)
                attachment.Duration = await GetAudioDuration(filePath);
            }

            _db.FileAttachments.Add(attachment);
            await _db.SaveChangesAsync(ct);

            var baseUrl = $"{Request.Scheme}://{Request.Host}";

            return Ok(new UploadFileResponse
            {
                Id = fileId,
                FileName = file.FileName,
                FileSize = file.Length,
                MimeType = file.ContentType,
                Url = $"{baseUrl}/api/files/files/{datePath}/{fileName}",
                ThumbnailUrl = hasThumbnail ? $"{baseUrl}/api/files/files/{datePath}/{thumbFileName}" : null,
                Duration = duration
            });
        }

        private async Task<int> GetAudioDuration(string filePath)
        {
            var mediaInfo = await FFProbe.AnalyseAsync(filePath);
            return (int)Math.Ceiling(mediaInfo.Duration.TotalSeconds);
        }

        //private async Task GenerateThumbnail(string sourcePath, string destPath, int width, int height)
        //{
        //    using var image = await Image.LoadAsync(sourcePath);

        //    // Вычисляем пропорции
        //    var ratio = Math.Min((double)width / image.Width, (double)height / image.Height);
        //    var newWidth = (int)(image.Width * ratio);
        //    var newHeight = (int)(image.Height * ratio);

        //    image.Mutate(x => x
        //        .Resize(newWidth, newHeight)
        //        .BackgroundColor(Color.White));  // белый фон для прозрачных PNG

        //    // Сохраняем как JPEG (экономит место)
        //    await image.SaveAsync(destPath, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder
        //    {
        //        Quality = 85  // хорошее качество при небольшом размере
        //    });
        //}
        private async Task GenerateImageThumbnailWithFfmpeg(string inputPath, string outputPath, int width, int height)
        {
            await FFMpegArguments
                .FromFileInput(inputPath)
                .OutputToFile(outputPath, true, options => options
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

        //private async Task GenerateVideoThumbnail(string videoPath, string thumbPath)
        //{
        //    await FFMpegArguments
        //        .FromFileInput(videoPath)
        //        .OutputToFile(thumbPath, false, options => options
        //            .Seek(TimeSpan.FromSeconds(5))
        //            .WithVideoCodec("mjpeg")
        //            .WithFrameOutputCount(1)
        //            .WithVideoFilters(f => f.Scale(320, 240)))
        //        .ProcessAsynchronously();
        //}

        [HttpGet("{*filePath}")]
        public async Task<IActionResult> GetFile(string filePath)
        {
            var fullPath = Path.Combine(_storageBasePath, filePath);

            if (!System.IO.File.Exists(fullPath))
                return NotFound();

            // Пока без проверок — только для теста
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

        public class UploadFileResponse
        {
            public Guid Id { get; set; }
            public string FileName { get; set; } = string.Empty;
            public long FileSize { get; set; }
            public string MimeType { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
            public string? ThumbnailUrl { get; set; }
            public int? Duration { get; set; }
        }
    }
}
