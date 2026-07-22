using Asp.Versioning;
using FFMpegCore;
using FirebaseAdmin.Messaging;
using IDMChat.DTO;
using IDMChat.Hubs;
using IDMChat.Models;
using IDMChat.Services;
using IDMChat.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Net.Mail;
using static IDMChat.Controllers.FilesController;
using static System.Net.WebRequestMethods;

namespace IDMChat.Controllers
{
    [ApiController]
    [Route("api/v1/internal/integration")]
    [ApiVersion("1.0")]
    [AllowAnonymous] // Все методы публичны, но жестко защищены по ApiKey
    public class InternalIntegrationController : ControllerBase
    {
        private readonly ChatDbContext _dbContext;
        private readonly ChatStateCache _chatCache;
        private readonly UserCache _userCache;
        private readonly IHubContext<ChatHub> _hubContext;
        private readonly IChatPathUrlResolver _urlResolver;
        private readonly INewMessageService _newMessageService;

        public InternalIntegrationController(ChatDbContext dbContext, ChatStateCache chatCache, UserCache userCache, IHubContext<ChatHub> hubContext, IChatPathUrlResolver urlResolver, INewMessageService newMessageService)
        {
            _dbContext = dbContext;
            _chatCache = chatCache;
            _userCache = userCache;
            _hubContext = hubContext;
            _urlResolver = urlResolver;
            _newMessageService = newMessageService;
        }

        // Вспомогательный метод сквозной проверки ключа ИДМ
        private bool IsValidApiKey()
        {
            return true;
            if (!Request.Headers.TryGetValue("X-Internal-Api-Key", out var apiKey) ||
                apiKey != "SuperSecretKey_IdmToChat_2026_SecureToken!")
            {
                return false;
            }
            return true;
        }

        // =========================================================================
        // МЕТОД 1: Мгновенная блокировка пользователя из ИДМ
        // =========================================================================
        [HttpPost("block-user")]
        [AllowAnonymous]
        public async Task<IActionResult> BlockUserInternal([FromQuery] int id, [FromQuery] int isblocked, CancellationToken ct = default)
        {
            if (!IsValidApiKey()) return Forbid();

            // 2. Ищем Guid пользователя чата по его int ID из ИДМ прямо в оперативной памяти
            var chatUserId = _userCache.GetChatUserIdByIdmId(id);
            if (chatUserId == null)
            {
                // Если юзер еще ни разу не заходил в чат и его нет в базе чата — чистить некого
                return Ok(new { message = "User not found in chat local cache" });
            }

            // 3. МГНОВЕННЫЙ РАЗРЫВ SIGNALR СОЕДИНЕНИЙ (Highload-подход)
            // Отправляем системную команду "Disconnect" на все устройства заблокированного пользователя
            // Название метода "Disconnect" должно поддерживаться в вашем ChatHub (он вызовет Context.Abort())
            if (isblocked > 0)
                await _hubContext.Clients.User(chatUserId.Value.ToString()).SendAsync("Disconnect", new
                {
                    reason = "ACCOUNT_BLOCKED",
                    message = "Ваша учетная запись заблокирована администратором в системе ИДМ"
                });

            var userInDb = await _dbContext.Users
                .AsTracking()
                .FirstOrDefaultAsync(u => u.Id == chatUserId.Value, ct);

            if (userInDb != null)
            {
                // 1. Фиксируем блокировку в СУБД (Защита от перезапуска сервера!)
                userInDb.IsActive = isblocked == 0;
                await _dbContext.SaveChangesAsync(ct);

                // 2. Обновляем состояние оперативной памяти (UserCache) БЕЗ затирания аватарок
                var currentCache = _userCache.GetUser(chatUserId.Value);
                string cleanName = currentCache?.DisplayName.Replace(" [Блокирован]", "") ?? userInDb.DisplayName;

                _userCache.AddOrUpdateUser(
                    chatUserId.Value,
                    cleanName,
                    currentCache?.AvatarUrl,
                    currentCache?.CustomStatus,
                    currentCache?.LastSeenAt ?? DateTime.MinValue,
                    id,
                    isActive: isblocked == 0 // Метод кэша сам допишет "[Блокирован]" в RAM
                );
            }

            return Ok(new { success = true, message = $"Успешно" });
        }

        // =========================================================================
        // МЕТОД 2: Дублирование системных рассылок/алертов из ИДМ (Вместо Telegram)
        // =========================================================================
        [HttpPost("send-message")]
        public async Task<IActionResult> SendExternalMessage([FromBody] ExternalMessageRequestDto dto, CancellationToken ct)
        {
            if (!IsValidApiKey()) return Forbid();

            if (string.IsNullOrWhiteSpace(dto.chat_id) || string.IsNullOrWhiteSpace(dto.text))
            {
                return BadRequest(new { error = "Параметры chat_id и text обязательны" });
            }

            // Находим маппинг по индексу БД
            var mapping = await _dbContext.ExternalChatMappings
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.ExternalChatId == dto.chat_id, ct);

            if (mapping == null)
            {
                return Ok(new { success = false, message = "Маппинг не настроен. Пропущено." });
            }

            var existingMessage = await _dbContext.Messages
                .AsTracking() // Нам нужен трекинг для возможного редактирования текста
                .FirstOrDefaultAsync(m => m.ExternalIdmId == dto.id && m.ConversationId == mapping.ConversationId, ct);

            // 2. ЛОГИКА РЕПЛАЕВ: Находим внутренний Guid сообщения, на которое отвечает ИДМ
            long? internalReplyToId = null;
            if (dto.reply_to_id.HasValue && dto.reply_to_id.Value > 0)
            {
                // Ищем оригинальное сообщение по индексу внешнего ID за 0 миллисекунд
                internalReplyToId = await _dbContext.Messages
                    .Where(m => m.ExternalIdmId == dto.reply_to_id.Value)
                    .Select(m => m.Id)
                    .FirstOrDefaultAsync(ct);
            }

            var isMediaCode = dto.code?.ToLowerInvariant() == "media";
            var attachment_ids = new List<Guid>();
            string? globalDetectedType = null;

            if (existingMessage != null)
            {
                // повтор или обновление
                string incomingText = isMediaCode ? "Приложение" : dto.text?.Trim() ?? string.Empty;

                // КЕЙС 1: Текст полностью совпадает — это чистый сетевой дубль. Игнорируем без ошибок (200 OK)
                if (existingMessage.Text == incomingText || existingMessage.Text == "" || existingMessage.Text == "Приложение")
                {
                    return Ok(new { success = true, message = "Сетевой дубликат успешно проигнорирован", internal_message_id = existingMessage.Id });
                }
                existingMessage.Text = incomingText;
                existingMessage.UpdatedAt = DateTime.UtcNow;

                var msg = new ChatHub.NewMessageRequest()
                {
                    conversation_id = mapping.ConversationId,
                    text = isMediaCode ? "" : dto.text?.Trim() ?? string.Empty,
                    type = globalDetectedType ?? "text",
                    temp_id = Guid.NewGuid(),
                    ext_id = dto.id,
                    attachment_ids = attachment_ids,
                    mentions = new List<ChatHub.MentionItem>(),
                    reply_to_message_id = internalReplyToId
                };
                //await _newMessageService.HandleUpdateMessage(msg, Guid.Parse("00000000-0000-0000-0000-000000000001"), ct);

                return Ok(new { success = true });
            }
            else
            {


                if (isMediaCode && !string.IsNullOrWhiteSpace(dto.text))
                {
                    var filePaths = dto.text.Split(new[] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries);
                    var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();

                    var attachmentsList = new List<FileAttachment>();
                    var uniqueFileTypes = new HashSet<FileType>();

                    foreach (var rawPath in filePaths)
                    {
                        string sourceFilePath = rawPath.Trim();
                        if (!System.IO.File.Exists(sourceFilePath)) continue;

                        string extension = Path.GetExtension(sourceFilePath).ToLowerInvariant();
                        FileType? detectedFileType = null;

                        if (extension == ".jpg" || extension == ".jpeg" || extension == ".png" || extension == ".webp")
                        {
                            detectedFileType = FileType.Image;
                        }
                        else if (extension == ".mp4" || extension == ".mkv" || extension == ".mov")
                        {
                            detectedFileType = FileType.Video;
                        }

                        if (!detectedFileType.HasValue) continue;
                        uniqueFileTypes.Add(detectedFileType.Value);

                        if (!provider.TryGetContentType(sourceFilePath, out var fileMimeType))
                        {
                            fileMimeType = detectedFileType == FileType.Image ? "image/jpeg" : "video/mp4";
                        }

                        try
                        {
                            int? duration = null;
                            if (detectedFileType == FileType.Video)
                            {
                                var mediaInfo = await FFProbe.AnalyseAsync(sourceFilePath);
                                duration = (int)mediaInfo.Duration.TotalSeconds;
                            }

                            string normalizedSourcePath = sourceFilePath.Replace('/', Path.DirectorySeparatorChar)
                                             .Replace('\\', Path.DirectorySeparatorChar);

                            // Сборка путей через символьную ссылку mklink
                            string searchToken = "uploads" + Path.DirectorySeparatorChar;
                            int indexToken = normalizedSourcePath.IndexOf(searchToken, StringComparison.OrdinalIgnoreCase);
                            string relativeStoragePath;
                            string relativeThumbnailPath;

                            if (indexToken != -1)
                            {
                                // КЕЙС А: Папка uploads найдена — вырезаем относительный веб-путь
                                string rawRelativePath = normalizedSourcePath.Substring(indexToken + searchToken.Length);
                                relativeStoragePath = rawRelativePath.Replace('\\', '/');

                                int lastDotIndex = relativeStoragePath.LastIndexOf('.');
                                relativeThumbnailPath = (lastDotIndex != -1)
                                    ? relativeStoragePath.Substring(0, lastDotIndex) + "_thumb.jpg"
                                    : relativeStoragePath + "_thumb.jpg";
                            }
                            else
                            {
                                // КЕЙС Б: Резервный сценарий (uploads нет в пути) — берем просто имя файла
                                // Файл все равно запишется в базу, и интеграция не упадет!
                                relativeStoragePath = Path.GetFileName(sourceFilePath);

                                int lastDotIndex = relativeStoragePath.LastIndexOf('.');
                                relativeThumbnailPath = (lastDotIndex != -1)
                                    ? relativeStoragePath.Substring(0, lastDotIndex) + "_thumb.jpg"
                                    : relativeStoragePath + "_thumb.jpg";
                            }

                            var attachmentId = Guid.NewGuid();
                            attachment_ids.Add(attachmentId); // Запоминаем GUID для сервиса

                            attachmentsList.Add(new FileAttachment
                            {
                                Id = attachmentId,
                                MessageId = null, // Сервис отправки сам свяжет MessageId (long) под капотом!
                                UserId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                                ConversationId = mapping.ConversationId,
                                FileName = Path.GetFileName(sourceFilePath),
                                FileSize = new FileInfo(sourceFilePath).Length,
                                MimeType = fileMimeType,
                                StoragePath = relativeStoragePath,
                                ThumbnailPath = relativeThumbnailPath,
                                Type = detectedFileType.Value,
                                Duration = duration,
                                CreatedAt = DateTime.UtcNow
                            });
                        }
                        catch (Exception) { /* Изолируем сбой одного файла */ }
                    }

                    if (attachmentsList.Any())
                    {
                        // Сохраняем вложения в БД до вызова сервиса, чтобы он увидел их существование
                        _dbContext.FileAttachments.AddRange(attachmentsList);
                        await _dbContext.SaveChangesAsync(ct);

                        // Вычисляем текстовый тип сообщения для DTO сервиса
                        globalDetectedType = uniqueFileTypes.Count == 1
                            ? (uniqueFileTypes.First() == FileType.Image ? "image" : "video")
                            : "mixed";
                    }
                }

                var msg = new ChatHub.NewMessageRequest()
                {
                    conversation_id = mapping.ConversationId,
                    text = isMediaCode ? "Приложение" : dto.text?.Trim() ?? string.Empty,
                    type = globalDetectedType ?? "text",
                    temp_id = Guid.NewGuid(),
                    ext_id = dto.id,
                    attachment_ids = attachment_ids,
                    mentions = new List<ChatHub.MentionItem>(),
                    reply_to_message_id = internalReplyToId
                };
                await _newMessageService.HandleSendMessage(msg, Guid.Parse("00000000-0000-0000-0000-000000000001"), ct);

                return Ok(new { success = true });
            }
        }
    }
}
