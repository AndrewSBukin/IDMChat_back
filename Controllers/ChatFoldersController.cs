using Asp.Versioning;
using IDMChat.DTO;
using IDMChat.Hubs;
using IDMChat.Middleware;
using IDMChat.Models;
using IDMChat.Utils;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace IDMChat.Controllers
{
    [Route("api/v{version:apiVersion}/chat-folders")]
    [ApiVersion("1.0")]
    [ApiController]
    public class ChatFoldersController : ControllerBase
    {
        private readonly ChatDbContext _db;
        private readonly IHubContext<ChatHub> _hubContext;
        private readonly ChatStateCache _chatCache;

        public ChatFoldersController(ChatDbContext db, IHubContext<ChatHub> hubContext, ChatStateCache chatCache)
        {
            _db = db;
            _hubContext = hubContext;
            _chatCache = chatCache;
        }

        [HttpGet]
        public async Task<ActionResult<ChatFoldersListResponseDto>> GetMyFolders(CancellationToken ct = default)
        {
            // Извлекаем ID авторизованного пользователя из токена (через ваше расширение)
            var userId = HttpContext.GetCurrentUserId();

            // Загружаем папки пользователя со всеми привязанными чатами
            var dbFolders = await _db.ChatFolders
                .Include(f => f.Items)
                .Where(f => f.UserId == userId)
                .OrderBy(f => f.Position) // Строго по position asc по ТЗ
                .ToListAsync(ct);

            // Маппим сущности БД в чистые структуры ChatFolderDto
            var folderDtos = dbFolders.Select(f => new ChatFolderDto
            {
                id = f.Id,
                title = f.Title,
                position = f.Position,
                created_at = f.CreatedAt,
                updated_at = f.UpdatedAt,

                // Массив всех чатов папки (в порядке добавления по Order)
                conversation_ids = f.Items
                    .OrderBy(i => i.Order)
                    .Select(i => i.ConversationId)
                    .ToList(),

                // Массив только закрепленных чатов (в порядке закрепа по PinnedOrder)
                pinned_conversation_ids = f.Items
                    .Where(i => i.IsPinned)
                    .OrderBy(i => i.PinnedOrder)
                    .Select(i => i.ConversationId)
                    .ToList()
            }).ToList();

            var allPinnedIds = await _db.ConversationMembers
                .AsNoTracking()
                .Where(cm => cm.UserId == userId && cm.IsPinned)
                .OrderBy(cm => cm.PinnedOrder) // Сортируем строго в кастомном порядке drag-and-drop
                .Select(cm => cm.ConversationId)
                .ToListAsync(ct);

            // Возвращаем обертку "folders", как просит фронтенд
            var response = new ChatFoldersListResponseDto
            {
                folders = folderDtos,
                all_folder_pinned_ids = allPinnedIds
            };

            return Ok(response);
        }

        [HttpPost]
        public async Task<IActionResult> CreateFolder([FromBody] CreateChatFolderRequestDto dto, CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();

            // 1. Ручной тримминг и проверка на пустоту (Пункт 4.4 ТЗ)
            var trimmedTitle = dto.title?.Trim();
            if (string.IsNullOrEmpty(trimmedTitle))
            {
                return BadRequest(new { error = new { code = "TITLE_REQUIRED", message = "Название папки не может быть пустым" } });
            }

            // 2. Проверка максимальной длины (Пункт 4.4 ТЗ)
            if (trimmedTitle.Length > 24)
            {
                return BadRequest(new { error = new { code = "TITLE_TOO_LONG", message = "Название папки не должно превышать 24 символа" } });
            }

            // 3. Проверка лимита количества папок у пользователя (Максимум 20 по Пункту 4.1)
            var currentFoldersCount = await _db.ChatFolders
                .CountAsync(f => f.UserId == userId, ct);

            if (currentFoldersCount >= 20)
            {
                return BadRequest(new { error = new { code = "FOLDER_LIMIT_EXCEEDED", message = "Можно создать максимум 20 папок" } });
            }

            var uniqueChatIds = new List<Guid>();

            if (dto.conversation_ids != null && dto.conversation_ids.Any())
            {
                // Исключаем дубликаты внутри запроса
                uniqueChatIds = dto.conversation_ids.Distinct().ToList();

                foreach (var chatId in uniqueChatIds)
                {
                    // Проверяем существование чата и состав участников через кэш
                    var cachedChat = await _chatCache.GetConversationAsync(chatId);

                    if (cachedChat == null)
                    {
                        return BadRequest(new { error = new { code = "CONVERSATION_NOT_FOUND", message = $"Диалог с ID {chatId} не найден" } });
                    }

                    // Проверяем, состоит ли текущий юзер в этом чате
                    if (!cachedChat.Members.Contains(userId))
                    {
                        return StatusCode(403, new { error = new { code = "ACCESS_DENIED", message = $"Вы не являетесь участником чата {chatId}" } });
                    }
                }
            }

            // 4. Вычисление автоматической позиции (position = max + 1 по Пункту 2.2)
            var maxPosition = await _db.ChatFolders
                .Where(f => f.UserId == userId)
                .Select(f => (int?)f.Position)
                .MaxAsync(ct) ?? -1;

            var newFolder = new ChatFolder
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Title = trimmedTitle,
                Position = maxPosition + 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _db.ChatFolders.Add(newFolder);
            await _db.SaveChangesAsync(ct);

            // 5. Привязка чатов (Пункт 2.2)
            if (uniqueChatIds.Any())
            {
                newFolder.Items = uniqueChatIds.Select((chatId, index) => new ChatFolderItem
                {
                    ConversationId = chatId,
                    Order = index,
                    IsPinned = false,
                    PinnedOrder = 0, 
                    FolderId = newFolder.Id
                }).ToList();
            }

            // Сохраняем в базу данных
            //_db.ChatFolderItems.Add(newFolder.Items.ToList());
            await _db.SaveChangesAsync(ct);

            // 6. Оповещаем все активные устройства пользователя через SignalR (Пункт 3 ТЗ)
            await SendFoldersChangedNotification(userId, ct);

            // 7. Формируем ответ строго по ТЗ
            var responseDto = new ChatFolderDto
            {
                id = newFolder.Id,
                title = newFolder.Title,
                position = newFolder.Position,
                created_at = newFolder.CreatedAt,
                updated_at = newFolder.UpdatedAt,
                conversation_ids = newFolder.Items.OrderBy(i => i.Order).Select(i => i.ConversationId).ToList(),
                pinned_conversation_ids = new List<Guid>()
            };

            // Возвращаем 201 Created
            return StatusCode(201, responseDto);
        }

        [HttpPatch("{id}")] // Используем Patch по ТЗ
        public async Task<IActionResult> RenameFolder(Guid id, [FromBody] RenameChatFolderRequestDto dto, CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();

            // 1. Ручной тримминг и проверка на пустоту (Пункт 4.4 ТЗ)
            var trimmedTitle = dto.title?.Trim();
            if (string.IsNullOrEmpty(trimmedTitle))
            {
                return BadRequest(new { error = new { code = "TITLE_REQUIRED", message = "Название папки не может быть пустым" } });
            }

            // 2. Проверка максимальной длины (Пункт 4.4 ТЗ)
            if (trimmedTitle.Length > 24)
                trimmedTitle = trimmedTitle.Substring(0, 24);

            // 3. Находим папку в БД, включая её элементы для сборки итогового DTO
            var folder = await _db.ChatFolders
                .Include(f => f.Items).AsTracking()
                .FirstOrDefaultAsync(f => f.Id == id && f.UserId == userId, ct);

            if (folder == null)
            {
                return NotFound(new { error = new { code = "FOLDER_NOT_FOUND", message = "Папка не найдена" } });
            }

            // 4. Обновляем поля
            folder.Title = trimmedTitle;
            folder.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync(ct);

            // 5. Синхронизируем все устройства пользователя через SignalR (Пункт 3 ТЗ)
            await SendFoldersChangedNotification(userId, ct);

            // 6. Формируем ответ по ТЗ (Пункт 2.3)
            var responseDto = new ChatFolderDto
            {
                id = folder.Id,
                title = folder.Title,
                position = folder.Position,
                created_at = folder.CreatedAt,
                updated_at = folder.UpdatedAt,
                conversation_ids = folder.Items.OrderBy(i => i.Order).Select(i => i.ConversationId).ToList(),
                pinned_conversation_ids = folder.Items.Where(i => i.IsPinned).OrderBy(i => i.PinnedOrder).Select(i => i.ConversationId).ToList()
            };

            return Ok(responseDto);
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteFolder(Guid id, CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();

            // 1. Находим папку в базе данных, принадлежащую текущему пользователю
            var folder = await _db.ChatFolders
                .Include(f => f.Items) // Подтягиваем вложенные элементы для удаления
                .FirstOrDefaultAsync(f => f.Id == id && f.UserId == userId, ct);

            if (folder == null)
            {
                // По ТЗ возвращаем 204 No Content, даже если папка не найдена (идемпотентность метода DELETE)
                // Либо, если вам нужен строгий контроль, можно вернуть 404. Но 204 безопаснее для фронта.
                return NoContent();
            }

            // 2. Удаляем связанные элементы чатов из папки
            if (folder.Items.Any())
            {
                _db.ChatFolderItems.RemoveRange(folder.Items);
            }

            // 3. Удаляем саму папку
            _db.ChatFolders.Remove(folder);
            await _db.SaveChangesAsync(ct);

            // 4. Оповещаем все активные устройства пользователя через SignalR (Пункт 3 ТЗ)
            // Отправляем массив папок БЕЗ удаленной папки
            await SendFoldersChangedNotification(userId, ct);

            // 5. Возвращаем 204 No Content по спецификации ТЗ
            return NoContent();
        }

        [HttpPut("reorder")]
        public async Task<IActionResult> ReorderFolders([FromBody] ReorderChatFoldersRequestDto dto, CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();

            // 1. Кастомная валидация: проверяем, что массив не пустой
            if (dto.folder_ids == null || dto.folder_ids.Count == 0)
            {
                return BadRequest(new { error = new { code = "FOLDER_IDS_REQUIRED", message = "Массив идентификаторов папок не может быть пустым" } });
            }

            if (dto.folder_ids.Count != dto.folder_ids.Distinct().Count())
            {
                return BadRequest(new { error = new { code = "DUPLICATE_FOLDER_IDS", message = "Массив не должен содержать повторяющиеся идентификаторы папок" } });
            }

            // 2. Загружаем из базы ВСЕ папки текущего пользователя
            var myFolders = await _db.ChatFolders
                .Where(f => f.UserId == userId).AsTracking()
                .ToListAsync(ct);

            if (dto.folder_ids.Count != myFolders.Count)
            {
                return BadRequest(new { error = new { code = "FOLDER_COUNT_MISMATCH", message = "Количество присланных папок не совпадает с количеством существующих папок пользователя" } });
            }

            // 3. Пересчитываем положение папок по индексам в пришедшем массиве (Пункт 2.5 ТЗ)
            bool isAnyFolderUpdated = false;

            var sqlBuilder = new System.Text.StringBuilder();
            sqlBuilder.AppendLine("UPDATE [ChatFolders]");
            sqlBuilder.AppendLine("SET [Position] = CASE [Id]");
            var sqlParams = new List<Microsoft.Data.SqlClient.SqlParameter>();

            for (int i = 0; i < dto.folder_ids.Count; i++)
            {
                var targetFolderId = dto.folder_ids[i];

                // Находим папку в списке загруженных из БД
                var folder = myFolders.FirstOrDefault(f => f.Id == targetFolderId);
                if (folder == null)
                {
                    return BadRequest(new { error = new { code = "FOLDER_NOT_FOUND_OR_ACCESS_DENIED", message = $"Папка с ID {targetFolderId} не найдена или вам не принадлежит" } });
                }
                // Обновляем Position только если он реально изменился, чтобы зря не дергать БД
                if (folder.Position != i)
                {
                    string paramName = $"@p{i}";
                    sqlBuilder.AppendLine($"WHEN {paramName} THEN {i}");
                    sqlParams.Add(new Microsoft.Data.SqlClient.SqlParameter(paramName, System.Data.SqlDbType.UniqueIdentifier)
                    {
                        Value = dto.folder_ids[i]
                    });
                    //folder.Position = i;
                    //folder.UpdatedAt = DateTime.UtcNow;
                    isAnyFolderUpdated = true;
                }
            }
            sqlBuilder.AppendLine("ELSE [Position] END,");

            string updatedAtParamName = "@p_updated_at";
            sqlBuilder.AppendLine($"[UpdatedAt] = {updatedAtParamName},");
            sqlParams.Add(new Microsoft.Data.SqlClient.SqlParameter(updatedAtParamName, System.Data.SqlDbType.DateTime2)
            {
                Value = DateTime.UtcNow
            });

            // Явно добавляем параметр UserId как UNIQUEIDENTIFIER (Guid)
            string userIdParamName = "@p_user_id";
            sqlBuilder.AppendLine($"WHERE [UserId] = {userIdParamName}"); // Добавляем фиктивное поле перед WHERE, если запятая осталась, либо убираем её из конструкции выше
            sqlParams.Add(new Microsoft.Data.SqlClient.SqlParameter(userIdParamName, System.Data.SqlDbType.UniqueIdentifier)
            {
                Value = userId
            });

            string finalSql = sqlBuilder.ToString()
                .Replace(",\r\nWHERE", "\r\nWHERE")
                .Replace(",\nWHERE", "\nWHERE");

            // 4. Сохраняем изменения в БД, если были реальные сдвиги
            if (isAnyFolderUpdated)
            {
                await _db.Database.ExecuteSqlRawAsync(finalSql, sqlParams, ct);
                _db.ChangeTracker.Clear();
                //await _db.SaveChangesAsync(ct);
            }

            // 5. Оповещаем все активные устройства пользователя через SignalR (Пункт 3 ТЗ)
            await SendFoldersChangedNotification(userId, ct);

            // 6. Формируем ответ по ТЗ: возвращаем полный актуальный список папок, отсортированных по position asc (Пункт 2.5)
            // Перезапрашивать БД не нужно — мы можем использовать коллекцию myFolders, которая уже находится в памяти бэкенда.
            // Но так как нам нужны conversation_ids, которые лежат в ChatFolderItems, мы применим повторную выборку, 
            // либо можно было изначально подтянуть Include(f => f.Items). Сделаем быструю проекцию:

            var dbFoldersSorted = await _db.ChatFolders
                .Include(f => f.Items)
                .Where(f => f.UserId == userId)
                .OrderBy(f => f.Position)
                .ToListAsync(ct);

            var responseDtos = dbFoldersSorted.Select(f => new ChatFolderDto
            {
                id = f.Id,
                title = f.Title,
                position = f.Position,
                created_at = f.CreatedAt,
                updated_at = f.UpdatedAt,
                conversation_ids = f.Items.OrderBy(i => i.Order).Select(i => i.ConversationId).ToList(),
                pinned_conversation_ids = f.Items.Where(i => i.IsPinned).OrderBy(i => i.PinnedOrder).Select(i => i.ConversationId).ToList()
            }).ToList();

            return Ok(new { folders = responseDtos });
        }

        [HttpPost("{id}/conversations")]
        public async Task<IActionResult> AddConversationsToFolder(Guid id, [FromBody] AddChatsToFolderRequestDto dto, CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();

            // 1. Валидация входных данных
            if (dto.conversation_ids == null || dto.conversation_ids.Count == 0)
            {
                return BadRequest(new { error = new { code = "CONVERSATION_IDS_REQUIRED", message = "Массив идентификаторов чатов не может быть пустым" } });
            }

            // 2. Находим папку и её текущие элементы
            var folder = await _db.ChatFolders
                .Include(f => f.Items).AsTracking()
                .FirstOrDefaultAsync(f => f.Id == id && f.UserId == userId, ct);

            if (folder == null)
            {
                return NotFound(new { error = new { code = "FOLDER_NOT_FOUND", message = "Папка не найдена" } });
            }

            // 3. Выделяем только новые уникальные чаты, которых ЕЩЕ НЕТ в папке (Пункт 2.6 ТЗ)
            var existingChatIds = folder.Items.Select(i => i.ConversationId).ToHashSet();

            var incomingNewChatIds = dto.conversation_ids
                .Distinct() // Игнорируем дубли внутри самого запроса
                .Where(chatId => !existingChatIds.Contains(chatId)) // Игнорируем уже присутствующие чаты
                .ToList();

            // Если все присланные чаты уже добавлены в папку — просто отдаем текущее состояние без ошибок
            if (!incomingNewChatIds.Any())
            {
                var currentFolderDto = MapToFolderDto(folder);
                return Ok(currentFolderDto);
            }

            // 2. ВАЛИДАЦИЯ СУЩЕСТВОВАНИЯ И ДОСТУПА В КЭШЕ (Исправляем ошибку Foreign Key!)
            foreach (var chatId in incomingNewChatIds)
            {
                var cachedChat = await _chatCache.GetConversationAsync(chatId);

                // Если чата вообще нет в базе (тот самый случай ошибки FK)
                if (cachedChat == null)
                {
                    return BadRequest(new { error = new { code = "CONVERSATION_NOT_FOUND", message = $"Диалог с ID {chatId} не существует" } });
                }

                // Если чат есть, но текущий пользователь из него вышел или не имеет доступа
                if (!cachedChat.Members.Contains(userId))
                {
                    return StatusCode(403, new { error = new { code = "ACCESS_DENIED", message = $"Вы не являетесь участником чата {chatId}" } });
                }
            }

            // 4. Проверяем жесткий лимит — максимум 200 чатов в папке (Пункт 4.1 ТЗ)
            int totalCountAfterAdd = folder.Items.Count + incomingNewChatIds.Count;
            if (totalCountAfterAdd > 200)
            {
                return BadRequest(new { error = new { code = "CHAT_LIMIT_EXCEEDED", message = "В одной папке может находиться не более 200 чатов" } });
            }

            // 5. Вычисляем текущий максимальный Order среди чатов, чтобы продолжить автоинкремент порядка
            var maxOrder = folder.Items.Any()
                ? folder.Items.Max(i => i.Order)
                : -1;

            // 6. Создаем новые записи связей
            var newItems = incomingNewChatIds.Select((chatId, index) => new ChatFolderItem
            {
                FolderId = folder.Id,
                ConversationId = chatId,
                Order = maxOrder + 1 + index, // Продолжаем сортировку в порядке добавления по ТЗ
                IsPinned = false,
                PinnedOrder = 0
            }).ToList();

            _db.ChatFolderItems.AddRange(newItems);

            // Обновляем дату изменения папки
            folder.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            // 7. Оповещаем все активные устройства пользователя через SignalR (Пункт 3 ТЗ)
            await SendFoldersChangedNotification(userId, ct);

            // 8. Перезапрашиваем обновленный объект и возвращаем 200 OK по ТЗ (Пункт 2.6)
            var updatedFolder = await _db.ChatFolders
                .Include(f => f.Items)
                .FirstAsync(f => f.Id == folder.Id, ct);

            return Ok(MapToFolderDto(updatedFolder));
        }

        [HttpDelete("{id}/conversations/{conversationId}")]
        public async Task<IActionResult> RemoveConversationFromFolder(Guid id, Guid conversationId, CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();

            // 1. Находим папку 
            var folder = await _db.ChatFolders
                .AsTracking()
                .Include(f => f.Items)
                .FirstOrDefaultAsync(f => f.Id == id && f.UserId == userId, ct);

            if (folder == null)
            {
                return NotFound(new { error = new { code = "FOLDER_NOT_FOUND", message = "Папка не найдена" } });
            }

            // 2. Ищем элемент чата внутри папки
            var targetItem = folder.Items
                .FirstOrDefault(i => i.ConversationId == conversationId);

            // Если чата и так нет в этой папке — просто возвращаем текущий DTO без ошибок (идемпотентность)
            if (targetItem == null)
            {
                return Ok(MapToFolderDto(folder));
            }

            // 3. Удаляем элемент из БД (EF Core сам сгенерирует DELETE по составному ключу благодаря трекингу)
            _db.ChatFolderItems.Remove(targetItem);

            // Обновляем дату модификации папки
            folder.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            // 4. Оповещаем все активные устройства пользователя через SignalR (Пункт 3 ТЗ)
            await SendFoldersChangedNotification(userId, ct);

            // 5. Возвращаем обновленный объект папки по ТЗ (Пункт 2.7)
            // Перезапрашиваем из базы с NoTracking, так как нам нужна чистая выборка для отправки
            var updatedFolder = await _db.ChatFolders
                .AsNoTracking()
                .Include(f => f.Items)
                .FirstAsync(f => f.Id == folder.Id, ct);

            return Ok(MapToFolderDto(updatedFolder));
        }

        [HttpPut("{id}/pins")]
        public async Task<IActionResult> PinChatsInFolder(string id, [FromBody] PinChatsInFolderRequestDto dto, CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();

            // 1. Общая валидация: лимит не более 5 закрепов (Пункт 2.8 ТЗ)
            if (dto.conversation_ids == null)
                dto.conversation_ids = new List<Guid>();

            // Исключаем дубликаты, если фронт случайно прислал один ID дважды в массиве закрепов
            var incomingPinnedIds = dto.conversation_ids.Distinct().ToList();

            if (incomingPinnedIds.Count > 5)
            {
                return BadRequest(new { error = new { code = "PIN_LIMIT_EXCEEDED", message = "Можно закрепить максимум 5 чатов в одном табе" } });
            }

            // ==========================================
            // КЕЙС А: Обработка виртуального таба "all"
            // ==========================================
            if (id.ToLower() == "all")
            {
                // Находим все подписки пользователя на чаты
                var myChatConnections = await _db.ConversationMembers
                    .AsTracking()
                    .Where(cm => cm.UserId == userId)
                    .ToListAsync(ct);

                // Проверяем, что все присланные ID чатов действительно существуют и пользователь в них состоит
                var validMyChatIds = myChatConnections.Select(cm => cm.ConversationId).ToHashSet();
                if (incomingPinnedIds.Any(chatId => !validMyChatIds.Contains(chatId)))
                {
                    return BadRequest(new { error = new { code = "INVALID_CONVERSATION_ID", message = "Один или несколько чатов не найдены среди ваших диалогов" } });
                }

                // Массово пересчитываем флаги IsPinned в вашей существующей таблице
                foreach (var connection in myChatConnections)
                {
                    bool shouldBePinned = incomingPinnedIds.Contains(connection.ConversationId);
                    int pinIndex = incomingPinnedIds.IndexOf(connection.ConversationId);
                    if (pinIndex == -1) pinIndex = 0;
                    connection.IsPinned = shouldBePinned;
                    connection.PinnedOrder = pinIndex;
                }

                await _db.SaveChangesAsync(ct);

                // Инвалидируем кэш чатов, так как изменились глобальные закрепы таба "Все чаты"
                _chatCache.Invalidate(userId);

                // Оповещаем устройства об изменении папок (чтобы обновился глобальный стейт, если нужно)
                await SendFoldersChangedNotification(userId, ct);

                // Для виртуального таба "all" по ТЗ папки-записи нет, возвращаем пустой объект или статус 200
                return Ok(new { message = "Закрепы для таба 'Все чаты' успешно обновлены" });
            }

            // ==========================================
            // КЕЙС Б: Обработка обычной кастомной папки
            // ==========================================
            if (!Guid.TryParse(id, out var folderId))
            {
                return BadRequest(new { error = new { code = "INVALID_FOLDER_ID", message = "Неверный формат идентификатора папки" } });
            }

            // Находим папку С ТРЕКИНГОМ
            var folder = await _db.ChatFolders
                .AsTracking()
                .Include(f => f.Items)
                .FirstOrDefaultAsync(f => f.Id == folderId && f.UserId == userId, ct);

            if (folder == null)
            {
                return NotFound(new { error = new { code = "FOLDER_NOT_FOUND", message = "Папка не найдена" } });
            }

            // Валидация: Каждый присланный ID обязан присутствовать в этой папке, иначе 400 (Пункт 2.8 ТЗ)
            var folderChatIds = folder.Items.Select(i => i.ConversationId).ToHashSet();
            if (incomingPinnedIds.Any(chatId => !folderChatIds.Contains(chatId)))
            {
                return BadRequest(new { error = new { code = "CHAT_NOT_IN_FOLDER", message = "Можно закреплять только те чаты, которые добавлены в эту папку" } });
            }

            // Сбрасываем старые закрепы и проставляем новые строго в пришедшем порядке индексов массива
            foreach (var item in folder.Items)
            {
                int pinIndex = incomingPinnedIds.IndexOf(item.ConversationId);
                if (pinIndex != -1)
                {
                    item.IsPinned = true;
                    item.PinnedOrder = pinIndex; // Порядок закрепа 0, 1, 2...
                }
                else
                {
                    item.IsPinned = false;
                    item.PinnedOrder = 0;
                }
            }

            folder.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            // Оповещаем все активные устройства пользователя через SignalR (Пункт 3 ТЗ)
            await SendFoldersChangedNotification(userId, ct);

            // Возвращаем обновленный объект папки по ТЗ (Пункт 2.8)
            var updatedFolder = await _db.ChatFolders
                .AsNoTracking()
                .Include(f => f.Items)
                .FirstAsync(f => f.Id == folder.Id, ct);

            return Ok(MapToFolderDto(updatedFolder));
        }

        private ChatFolderDto MapToFolderDto(ChatFolder f)
        {
            return new ChatFolderDto
            {
                id = f.Id,
                title = f.Title,
                position = f.Position,
                created_at = f.CreatedAt,
                updated_at = f.UpdatedAt,
                conversation_ids = f.Items.OrderBy(i => i.Order).Select(i => i.ConversationId).ToList(),
                pinned_conversation_ids = f.Items.Where(i => i.IsPinned).OrderBy(i => i.PinnedOrder).Select(i => i.ConversationId).ToList()
            };
        }

        private async Task SendFoldersChangedNotification(Guid userId, CancellationToken ct = default)
        {
            // 1. Вычитываем из базы ВСЕ папки пользователя со всеми вложенными элементами
            var dbFolders = await _db.ChatFolders
                .Include(f => f.Items)
                .Where(f => f.UserId == userId)
                .OrderBy(f => f.Position) // Строго сортируем по position asc (Пункт 2.1)
                .ToListAsync(ct);

            // 2. Маппим сущности БД в чистые структуры ChatFolderDto
            var folderDtos = dbFolders.Select(f => new ChatFolderDto
            {
                id = f.Id,
                title = f.Title,
                position = f.Position,
                created_at = f.CreatedAt,
                updated_at = f.UpdatedAt,

                // Обычные чаты папки (сохраняем порядок добавления по Order)
                conversation_ids = f.Items
                    .OrderBy(i => i.Order)
                    .Select(i => i.ConversationId)
                    .ToList(),

                // Закрепленные чаты папки (сохраняем порядок закрепа по PinnedOrder)
                pinned_conversation_ids = f.Items
                    .Where(i => i.IsPinned)
                    .OrderBy(i => i.PinnedOrder)
                    .Select(i => i.ConversationId)
                    .ToList()
            }).ToList();

            var allPinnedIds = await _db.ConversationMembers
                .AsNoTracking()
                .Where(cm => cm.UserId == userId && cm.IsPinned)
                .OrderBy(cm => cm.PinnedOrder) // Сортируем строго в кастомном порядке drag-and-drop
                .Select(cm => cm.ConversationId)
                .ToListAsync(ct);

            // 3. Отправляем ВСЕМ активным устройствам данного конкретного пользователя (Clients.User)
            // Название события строго по ТЗ: "folders_changed"
            await _hubContext.Clients.User(userId.ToString()).SendAsync("folders_changed", new
            {
                folders = folderDtos,
                all_folder_pinned_ids = allPinnedIds
            }, ct);
        }
    }


}
