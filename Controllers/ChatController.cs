using Asp.Versioning;
using IDMChat.Domain;
using IDMChat.DTO;
using IDMChat.Hubs;
using IDMChat.Middleware;
using IDMChat.Models;
using IDMChat.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.RegularExpressions;
using static IDMChat.Controllers.ConversationsController;
using static IDMChat.Controllers.FilesController;

namespace IDMChat.Controllers
{

    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    [Authorize]
    [ApiVersion("1.0")]
    public class ConversationsController : ControllerBase
    {
        private readonly ILogger<ConversationsController> _logger;
        private readonly ChatDbContext _db;
        private readonly ChatStateCache _cache;
        private readonly IHubContext<ChatHub> _hubContext;
        private readonly string _storageBasePath;

        public ConversationsController(
            ChatDbContext dbContext, 
            ILogger<ConversationsController> logger,
            ChatStateCache cache,
            IHubContext<ChatHub> hubContext, 
            IConfiguration configuration)
        {
            _db = dbContext;
            _logger = logger;
            _cache = cache;
            _hubContext = hubContext;
            _storageBasePath = configuration["Storage:BasePath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "uploads");
        }

        #region Conversations
        [HttpGet]
        public async Task<ActionResult<ConversationsResponse>> GetConversations([FromQuery] int limit = 50, [FromQuery] int offset = 0, CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();

            // 1. Один запрос: все чаты + участники + последнее сообщение
            var conversationsData = await _db.ConversationMembers
                .Where(cm => cm.UserId == userId && !cm.Conversation.IsDeleted)
                .OrderByDescending(cm => cm.IsPinned)
                .ThenByDescending(cm => cm.Conversation.UpdatedAt)
                .Skip(offset)
                .Take(limit)
                .Select(cm => new
                {
                    cm.IsPinned,
                    cm.IsMuted,
                    cm.UnreadCount,
                    Conversation = cm.Conversation,

                    // Участники (только для group, для direct - все)
                    Members = cm.Conversation.Type == ConversationType.group
                        ? cm.Conversation.Members
                        .Where(m => !m.Conversation.IsDeleted)
                        .Select(m => new MemberResponse
                        {
                            id = m.UserId,
                            display_name = m.User.DisplayName,
                            avatar_url = m.User.AvatarUrl,
                            status = m.User.IsOnline ? "online" : "offline"
                        }).ToList()
                        : cm.Conversation.Members
                            .Where(m => m.UserId != userId)
                            .Select(m => new MemberResponse
                            {
                                id = m.UserId,
                                display_name = m.User.DisplayName,
                                avatar_url = m.User.AvatarUrl,
                                status = m.User.IsOnline ? "online" : "offline"
                            }).ToList(),

                    // Последнее сообщение (из денормализованного поля)
                    LastMessage = cm.Conversation.LastMessageId != null
                        ? new LastMessageResponse
                        {
                            id = cm.Conversation.LastMessage!.Id,
                            text = cm.Conversation.LastMessage.Text.Length > 100
                                ? cm.Conversation.LastMessage.Text.Substring(0, 100) + "..."
                                : cm.Conversation.LastMessage.Text,
                            type = cm.Conversation.LastMessage.Type.ToString().ToLower(),
                            sender_id = cm.Conversation.LastMessage.SenderId,
                            created_at = cm.Conversation.LastMessage.CreatedAt
                        }
                        : null
                })
                .ToListAsync(ct);

            // 2. Получить total (отдельный запрос, но легкий)
            var total = await _db.ConversationMembers
                .Where(cm => cm.UserId == userId && !cm.Conversation.IsDeleted)
                .CountAsync(ct);

            var conversations = conversationsData.Select(data => new ConversationResponse
            {
                id = data.Conversation.Id,
                type = data.Conversation.Type.ToString(),
                name = data.Conversation.Type == ConversationType.direct? data.Members?.FirstOrDefault()?.display_name : data.Conversation.Name,
                avatar_url = data.Conversation.AvatarUrl,
                is_pinned = data.IsPinned,
                is_muted = data.IsMuted,
                members = data.Members,
                last_message = data.LastMessage,
                unread_count = data.UnreadCount,
                updated_at = data.Conversation.UpdatedAt
            }).ToList();

            return Ok(new ConversationsResponse
            {
                Conversations = conversations,
                Total = total
            });
        }

        [HttpGet("pinned")]
        public async Task<ActionResult<ConversationsResponse>> GetPinnedConversations(CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();

            // 1. Один запрос: все чаты + участники + последнее сообщение
            var conversationsData = await _db.ConversationMembers
                .Where(cm => cm.UserId == userId && !cm.Conversation.IsDeleted && cm.IsPinned)
                .OrderByDescending(cm => cm.Conversation.UpdatedAt)
                .Select(cm => new
                {
                    cm.IsPinned,
                    cm.IsMuted,
                    cm.UnreadCount,
                    Conversation = cm.Conversation,

                    // Участники (только для group, для direct - все)
                    Members = cm.Conversation.Type == ConversationType.group
                        ? cm.Conversation.Members
                        .Where(m => !m.Conversation.IsDeleted)
                        .Select(m => new MemberResponse
                        {
                            id = m.UserId,
                            display_name = m.User.DisplayName,
                            avatar_url = m.User.AvatarUrl,
                            status = m.User.IsOnline ? "online" : "offline"
                        }).ToList()
                        : cm.Conversation.Members
                            .Where(m => m.UserId != userId)
                            .Select(m => new MemberResponse
                            {
                                id = m.UserId,
                                display_name = m.User.DisplayName,
                                avatar_url = m.User.AvatarUrl,
                                status = m.User.IsOnline ? "online" : "offline"
                            }).ToList(),

                    // Последнее сообщение (из денормализованного поля)
                    LastMessage = cm.Conversation.LastMessageId != null
                        ? new LastMessageResponse
                        {
                            id = cm.Conversation.LastMessage!.Id,
                            text = cm.Conversation.LastMessage.Text.Length > 100
                                ? cm.Conversation.LastMessage.Text.Substring(0, 100) + "..."
                                : cm.Conversation.LastMessage.Text,
                            type = cm.Conversation.LastMessage.Type.ToString().ToLower(),
                            sender_id = cm.Conversation.LastMessage.SenderId,
                            created_at = cm.Conversation.LastMessage.CreatedAt
                        }
                        : null
                })
                .ToListAsync(ct);

            // 2. Получить total (отдельный запрос, но легкий)
            var total = await _db.ConversationMembers
                .Where(cm => cm.UserId == userId && !cm.Conversation.IsDeleted)
                .CountAsync(ct);

            var conversations = conversationsData.Select(data => new ConversationResponse
            {
                id = data.Conversation.Id,
                type = data.Conversation.Type.ToString(),
                name = data.Conversation.Type == ConversationType.direct ? data.Members?.FirstOrDefault()?.display_name : data.Conversation.Name,
                avatar_url = data.Conversation.AvatarUrl,
                is_pinned = data.IsPinned,
                is_muted = data.IsMuted,
                members = data.Members,
                last_message = data.LastMessage,
                unread_count = data.UnreadCount,
                updated_at = data.Conversation.UpdatedAt
            }).ToList();

            return Ok(new ConversationsResponse
            {
                Conversations = conversations,
                Total = total
            });
        }

        [HttpPost]
        public async Task<IActionResult> CreateConversation([FromBody] CreateConversationRequest request, CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();
            var currentUser = await _db.Users.FindAsync(new object[] { userId }, ct);
            var idm = "";
            if (!Enum.TryParse<ConversationType>(request.Type, true, out var requestChatType))
                return UnprocessableEntity(new { error = new { code = "INVALID_FORMAT", message = $"Неверный тип: {request.Type}" } });
            // 1. Валидация
            if (requestChatType == ConversationType.direct)
            {
                if (request.MemberIds?.Count != 1)
                    return BadRequest(new { error = new { code = "DIRECT_REQUIRES_ONE_MEMBER", message = "Для создания чата необходим собеседник" } });

                // Проверка: чат уже существует?
                var existing = await _db.ConversationMembers
                    .Where(cm => cm.UserId == userId)
                    .Select(cm => cm.Conversation)
                    .Where(c => c.Type == ConversationType.direct)
                    .SelectMany(c => c.Members)
                    .Where(cm => request.MemberIds.Contains(cm.UserId))
                    .AnyAsync(ct);

                if (existing)
                    return Conflict(new { error = "CONVERSATION_EXISTS", message = "Такая беседа уже сущствует" });

                // Проверка компании
                var targetUser = await _db.Users.FindAsync(new object[] { request.MemberIds[0] }, ct);

                if (currentUser.idm != targetUser.idm && currentUser.Role != UserRole.Admin)
                    return NotFound(new { error = "USER_NOT_FOUND", message = "Пользователь не найден" });

                idm = currentUser.idm;
            }
            else // Group
            {
                //if (request.MemberIds?.Count < 1)
                //    return BadRequest(new { error = "GROUP_REQUIRES_AT_LEAST_TWO_MEMBERS", message = "Для создания группы необходим собеседник" });

                if (string.IsNullOrWhiteSpace(request.Name))
                    return BadRequest(new { error = "GROUP_NAME_REQUIRED", message = "Для создания группы необходимо ввести название" });

                if (currentUser.Role != UserRole.Admin)
                {
                    // Проверка компании для всех участников
                    var companyCodes = await _db.Users
                        .Where(u => request.MemberIds.Contains(u.Id))
                        .Select(u => u.idm)
                        .Distinct()
                        .ToListAsync(ct);

                    if (companyCodes.Count > 1 || (companyCodes.Count == 1 && companyCodes[0] != currentUser.idm))
                        return BadRequest(new { error = "MIXED_USERS_IN_GROUP", message = "Пользователи из разных компаний." });
                    if (companyCodes.Count == 0)
                        idm = currentUser.idm;
                    else
                        idm = companyCodes[0];
                }
                else
                {
                    if (request.MemberIds?.Count == 0)
                        idm = currentUser.idm;
                    else
                    {
                        idm = (await _db.Users.FirstOrDefaultAsync(u => request.MemberIds.First() == u.Id)).idm;
                    }
                }
            }

            // 2. Создание чата
            var conversation = new Conversation
            {
                Id = Guid.NewGuid(),
                Type = requestChatType,
                Name = requestChatType == ConversationType.group ? request.Name : null,
                AvatarUrl = request.AvatarUrl,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Idm = idm
            };

            // 3. Добавление участников
            var allMemberIds = request.MemberIds.ToList();
            allMemberIds.Add(userId);
            allMemberIds = allMemberIds.Distinct().ToList();

            var members = new List<ConversationMember>();
            foreach (var memberId in allMemberIds)
            {
                members.Add(new ConversationMember
                {
                    ConversationId = conversation.Id,
                    UserId = memberId,
                    IsAdmin = requestChatType == ConversationType.group && memberId == userId, // создатель - админ
                    IsPinned = false,
                    IsMuted = false,
                    UnreadCount = 0,
                    JoinedAt = DateTime.UtcNow,
                    LastReadMessageId = null
                });
            }

            _db.Conversations.Add(conversation);
            await _db.SaveChangesAsync(ct);

            _db.ConversationMembers.AddRange(members);
            await _db.SaveChangesAsync(ct);

            // 4. Системное сообщение для группы
            if (requestChatType == ConversationType.group)
            {
                var systemMessage = new Message
                {
                    ConversationId = conversation.Id,
                    SenderId = userId,
                    Text = $"{currentUser.DisplayName} создал группу",
                    Type = MessageType.System,
                    CreatedAt = DateTime.UtcNow,
                    SentAt = DateTime.UtcNow,
                    IsDeleted = false,
                    ChannelId = 0
                };
                _db.Messages.Add(systemMessage);
                await _db.SaveChangesAsync(ct);

                conversation.LastMessageId = systemMessage.Id;
                conversation.LastMessageText = systemMessage.Text;
                conversation.LastMessageSenderId = userId;
                conversation.LastMessageCreatedAt = systemMessage.CreatedAt;

            }

            // 5. Сохранение
            await _db.SaveChangesAsync(ct);

            // 6. Обновить кэш
            //_cache.InvalidateUserConversations(userId);
            //foreach (var memberId in allMemberIds)
            //{
            //    _cache.InvalidateUserConversations(memberId);
            //}

            // 7. Вернуть объект чата
            var response = await BuildConversationResponse(conversation.Id, userId, ct);

            // Уведомление участников (кроме себя)
            var otherMembers = allMemberIds.Where(id => id != userId).ToList();
            foreach (var newMemberId in otherMembers)
            {
                var fullConversation = await BuildConversationResponse(conversation.Id, newMemberId, ct);
                await _hubContext.Clients
                    .User(newMemberId.ToString())
                    .SendAsync("conversation_new", fullConversation, ct);
            }

            return CreatedAtAction(nameof(GetConversation), new { id = conversation.Id }, response);
            //return StatusCode(201, response);
        }

        /// <summary>
        /// Обновить название или аватар группы (только для администратора)
        /// </summary>
        [HttpPatch("{id}")]
        public async Task<ActionResult<ConversationResponse>> UpdateConversation(Guid id, [FromBody] UpdateConversationRequest request, CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();

            // 1. Находим чат и проверяем права
            var conversation = await _db.Conversations
                .FirstOrDefaultAsync(c => c.Id == id, ct);

            if (conversation == null)
                return NotFound(new { error = new { code = "CONVERSATION_NOT_FOUND", message = "Диалог не найден" } });

            // 2. Только групповые чаты можно обновлять
            if (conversation.Type != ConversationType.group)
                return BadRequest(new { error = new { code = "NOT_GROUP", message = "Только групповые чаты можно обновлять" } });

            // 3. Проверка: является ли пользователь администратором группы
            var member = await _db.ConversationMembers
                .FirstOrDefaultAsync(cm => cm.ConversationId == id && cm.UserId == userId, ct);

            if (member == null)
                return NotFound(new { error = new { code = "NOT_MEMBER", message = "Вы не участник чата" } });

            if (!member.IsAdmin)
                return StatusCode(403, new { error = new { code = "NOT_ADMIN", message = "Только администратор может изменять настройки группы" } });

            // 4. Обновляем поля (только те, что переданы)
            var hasChanges = false;

            if (!string.IsNullOrWhiteSpace(request.Name))
            {
                conversation.Name = request.Name;
                hasChanges = true;
            }

            if (!string.IsNullOrWhiteSpace(request.AvatarUrl))
            {
                conversation.AvatarUrl = request.AvatarUrl;
                hasChanges = true;
            }

            if (!hasChanges)
                return BadRequest(new { error = new { code = "NO_FIELDS_TO_UPDATE", message = "Нет полей для обновления" } });

            // 5. Обновляем timestamp
            conversation.UpdatedAt = DateTime.UtcNow;

            _db.Conversations.Update(conversation);
            await _db.SaveChangesAsync(ct);


            // 6. Инвалидируем кэш (если используется)
            _cache.Invalidate(id);

            // 7. Возвращаем обновлённый объект чата
            var response = await BuildConversationResponse(conversation.Id, userId, ct);
            await _hubContext.Clients.Group(id.ToString()).SendAsync("conversation_updated", response);

            return Ok(response);
        }

        /// <summary>
        /// Покинуть чат (direct) или удалить группу (только администратор)
        /// </summary>
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteConversation(Guid id, CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();

            // 1. Находим чат и проверяем членство
            var conversation = await _db.Conversations.AsTracking().FirstOrDefaultAsync(c => c.Id == id, ct);

            if (conversation == null)
                return NotFound(new { error = new { code = "CONVERSATION_NOT_FOUND", message = "Диалог не найден" } });

            // 2. Находим текущего участника
            var member = await _db.ConversationMembers
                .FirstOrDefaultAsync(cm => cm.ConversationId == id && cm.UserId == userId, ct);

            if (member == null)
                return NotFound(new { error = new { code = "NOT_MEMBER", message = "Вы не участник чата" } });

            // 3. Direct чат — просто удаляем участника (покидаем)
            if (conversation.Type == ConversationType.direct)
            {
                _db.ConversationMembers.Remove(member);

                // Проверяем, остался ли ещё участник
                var remainingMembers = await _db.ConversationMembers
                    .CountAsync(cm => cm.ConversationId == id, ct);

                if (remainingMembers == 0)
                {
                    _db.Conversations.Remove(conversation);
                }

                await _db.SaveChangesAsync(ct);

                // Инвалидируем кэш
                _cache.Invalidate(id);

                return NoContent();
            }

            // 4. Group чат — только администратор может удалить
            if (conversation.Type == ConversationType.group)
            {
                if (!member.IsAdmin)
                    return StatusCode(403, new { error = new { code = "NOT_ADMIN", message = "Только администратор может удалить группу" } });

                conversation.IsDeleted = true;
                conversation.DeletedAt = DateTime.UtcNow;
                conversation.DeletedBy = userId;

                // Участников из чата не удаляем
                // Сообщения не трогаем
                await _db.SaveChangesAsync(ct);

                // Инвалидируем кэш
                _cache.Invalidate(id);

                return NoContent();
            }

            return BadRequest(new { error = new { code = "UNKNOWN_TYPE", message = "Неизвестный тип чата" } });
        }

        /// <summary>
        /// Закрепить или открепить чат у текущего пользователя
        /// </summary>
        [HttpPatch("{id}/pin")]
        public async Task<IActionResult> PinConversation(Guid id, [FromBody] PinRequest request, CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();

            // Находим запись участника
            var member = await _db.ConversationMembers.AsTracking()
                .FirstOrDefaultAsync(cm => cm.ConversationId == id && cm.UserId == userId, ct);

            if (member == null)
                return NotFound(new { error = new { code = "NOT_MEMBER", message = "Вы не участник чата" } });

            // Обновляем
            member.IsPinned = request.is_pinned;
            await _db.SaveChangesAsync(ct);

            var response = await BuildConversationResponse(id, userId, ct);
            await _hubContext.Clients.Group(id.ToString()).SendAsync("conversation_updated", response);

            // Инвалидируем кэш (чтобы обновился порядок сортировки)
            _cache.Invalidate(id);

            return Ok(new { is_pinned = member.IsPinned });
        }

        /// <summary>
        /// Включить или отключить push-уведомления для чата
        /// </summary>
        [HttpPatch("{id}/mute")]
        public async Task<IActionResult> MuteConversation(Guid id, [FromBody] MuteRequest request, CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();

            // Находим запись участника
            var member = await _db.ConversationMembers.AsTracking()
                .FirstOrDefaultAsync(cm => cm.ConversationId == id && cm.UserId == userId, ct);

            if (member == null)
                return NotFound(new { error = new { code = "NOT_MEMBER", message = "Вы не участник чата" } });

            // Обновляем
            member.IsMuted = request.is_muted;
            await _db.SaveChangesAsync(ct);

            var response = await BuildConversationResponse(id, userId, ct);
            await _hubContext.Clients.Group(id.ToString()).SendAsync("conversation_updated", response);

            // Обновляем кэш (чтобы при отправке сообщений проверять is_muted)
            _cache.UpdateMuteStatus(id, userId, request.is_muted);

            return Ok(new { is_muted = member.IsMuted });
        }

        /// <summary>
        /// Добавить участников в группу (только для администратора)
        /// </summary>
        [HttpPost("{id}/members")]
        public async Task<IActionResult> AddMembers(Guid id, [FromBody] AddMembersRequest request, CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();

            // 1. Находим чат
            var conversation = await _db.Conversations.AsTracking()
                .FirstOrDefaultAsync(c => c.Id == id, ct);

            if (conversation == null)
                return NotFound(new { error = new { code = "CONVERSATION_NOT_FOUND", message = "Диалог не найден" } });

            // 2. Только групповые чаты
            if (conversation.Type != ConversationType.group)
                return BadRequest(new { error = new { code = "NOT_GROUP", message = "Только в групповые чаты можно добавлять участников" } });

            // 3. Проверка: пользователь — администратор?
            var isAdmin = await _db.ConversationMembers
                .AnyAsync(cm => cm.ConversationId == id && cm.UserId == userId && cm.IsAdmin, ct);

            if (!isAdmin)
                return StatusCode(403, new { error = new { code = "NOT_ADMIN", message = "Только администратор может добавлять участников" } });

            // 4. Получаем текущих участников
            var existingMemberIds = (await _db.ConversationMembers
                .Where(cm => cm.ConversationId == id)
                .Select(cm => cm.UserId)
                .ToListAsync(ct))
                .ToHashSet();

            // 5. Фильтруем новых участников (которых ещё нет в чате)
            var newMemberIds = request.MemberIds
                .Where(mid => !existingMemberIds.Contains(mid))
                .Distinct()
                .ToList();

            if (!newMemberIds.Any())
                return Ok(new { added = 0, message = "Нет новых участников для добавления" });

            // 6. Проверка компании (если не админ)
            var currentUser = await _db.Users.FindAsync(new object[] { userId }, ct);
            if (currentUser.Role != UserRole.Admin)
            {
                var companyCodes = await _db.Users
                    .Where(u => newMemberIds.Contains(u.Id))
                    .Select(u => u.idm)
                    .Distinct()
                    .ToListAsync(ct);

                if (companyCodes.Count > 1 || (companyCodes.Count == 1 && companyCodes[0] != currentUser.idm))
                    return Forbid();
            }

            // 7. Создаём записи участников
            var newMembers = newMemberIds.Select(memberId => new ConversationMember
            {
                ConversationId = id,
                UserId = memberId,
                IsAdmin = false,
                IsPinned = false,
                IsMuted = false,
                UnreadCount = 0,
                JoinedAt = DateTime.UtcNow,
                LastReadMessageId = null
            }).ToList();

            _db.ConversationMembers.AddRange(newMembers);

            // 8. Системное сообщение о добавлении
            var currentUserDisplayName = currentUser.DisplayName;
            var addedNames = await _db.Users
                .Where(u => newMemberIds.Contains(u.Id))
                .Select(u => u.DisplayName)
                .ToListAsync(ct);

            var systemMessage = new Message
            {
                ConversationId = id,
                SenderId = userId,
                Text = $"{currentUserDisplayName} добавил(а): {string.Join(", ", addedNames)}",
                Type = MessageType.System,
                CreatedAt = DateTime.UtcNow,
                SentAt = DateTime.UtcNow,
                IsDeleted = false,
                ChannelId = 0
            };

            _db.Messages.Add(systemMessage);

            // 9. Обновляем LastMessage
            conversation.LastMessageId = systemMessage.Id;
            conversation.LastMessageText = systemMessage.Text.Length > 100
                ? systemMessage.Text[..100] + "..."
                : systemMessage.Text;
            conversation.LastMessageSenderId = userId;
            conversation.LastMessageCreatedAt = systemMessage.CreatedAt;
            conversation.UpdatedAt = systemMessage.CreatedAt;

            await _db.SaveChangesAsync(ct);

            // 10. Уведомления через хаб
            foreach (var newMemberId in newMemberIds)
            {
                var fullConversation = await BuildConversationResponse(id, newMemberId, ct);
                await _hubContext.Clients
                    .User(newMemberId.ToString())
                    .SendAsync("conversation_new", fullConversation);
            }

            // Уведомить остальных участников
            var otherMemberIds = existingMemberIds.Except(newMemberIds).ToList();
            foreach (var memberId in otherMemberIds)
            {
                await _hubContext.Clients
                    .User(memberId.ToString())
                    .SendAsync("members_added", new { conversationId = id, memberIds = newMemberIds, addedBy = userId });
            }

            // 11. Инвалидируем кэш
            _cache.Invalidate(id);

            return Ok(new { added = newMemberIds.Count, member_ids = newMemberIds });
        }

        /// <summary>
        /// Удалить участника из группы (только для администратора)
        /// </summary>
        [HttpDelete("{id}/members/{memberId}")]
        public async Task<IActionResult> RemoveMember(Guid id, Guid memberId, CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();

            // 1. Находим чат
            var conversation = await _db.Conversations.AsTracking()
                .FirstOrDefaultAsync(c => c.Id == id, ct);

            if (conversation == null)
                return NotFound(new { error = new { code = "CONVERSATION_NOT_FOUND", message = "Диалог не найден" } });

            // 2. Только групповые чаты
            if (conversation.Type != ConversationType.group)
                return BadRequest(new { error = new { code = "NOT_GROUP", message = "Только из групповых чатов можно удалять участников" } });

            // 3. Проверка: текущий пользователь — администратор?
            var isAdmin = await _db.ConversationMembers
                .AnyAsync(cm => cm.ConversationId == id && cm.UserId == userId && cm.IsAdmin, ct);

            if (!isAdmin)
                return StatusCode(403, new { error = new { code = "NOT_ADMIN", message = "Только администратор может удалять участников" } });

            // 4. Нельзя удалить самого себя (для выхода из группы есть DELETE /conversations/{id})
            if (userId == memberId)
                return BadRequest(new { error = new { code = "CANNOT_REMOVE_SELF", message = "Используйте DELETE /conversations/{id} для выхода из группы" } });

            // 5. Находим удаляемого участника
            var member = await _db.ConversationMembers
                .FirstOrDefaultAsync(cm => cm.ConversationId == id && cm.UserId == memberId, ct);

            if (member == null)
                return NotFound(new { error = new { code = "MEMBER_NOT_FOUND", message = "Участник не найден" } });

            // 6. Удаляем участника
            _db.ConversationMembers.Remove(member);

            // 7. Системное сообщение
            var currentUser = await _db.Users.FindAsync(new object[] { userId }, ct);
            var removedUser = await _db.Users.FindAsync(new object[] { memberId }, ct);

            var systemMessage = new Message
            {
                ConversationId = id,
                SenderId = userId,
                Text = $"{currentUser.DisplayName} удалил(а) {removedUser?.DisplayName ?? memberId.ToString()}",
                Type = MessageType.System,
                CreatedAt = DateTime.UtcNow,
                SentAt = DateTime.UtcNow,
                IsDeleted = false,
                ChannelId = 0
            };

            _db.Messages.Add(systemMessage);

            // 8. Обновляем LastMessage
            conversation.LastMessageId = systemMessage.Id;
            conversation.LastMessageText = systemMessage.Text.Length > 100
                ? systemMessage.Text[..100] + "..."
                : systemMessage.Text;
            conversation.LastMessageSenderId = userId;
            conversation.LastMessageCreatedAt = systemMessage.CreatedAt;
            conversation.UpdatedAt = systemMessage.CreatedAt;

            await _db.SaveChangesAsync(ct);

            // 9. Инвалидируем кэш
            _cache.Invalidate(id);

            return NoContent();
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<ConversationResponse>> GetConversation(Guid id, CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();
            //var currentUser = await _db.Users.FindAsync(new object[] { userId }, ct);

            // Проверка: пользователь в чате?
            var isMember = await _db.ConversationMembers
                .AnyAsync(cm => cm.ConversationId == id && cm.UserId == userId, ct);

            if (!isMember)
                return NotFound();

            var response = await BuildConversationResponse(id, userId, ct);
            return Ok(response);
        }
        #endregion

        #region Messages
        [HttpGet("{conversationId}/messages")]
        public async Task<IActionResult> GetMessages(
            Guid conversationId,
            [FromQuery] int limit = 50,
            [FromQuery] long? before = null,  // загрузить СТАРШЕ (история)
            [FromQuery] long? after = null,   // загрузить НОВЕЕ
            CancellationToken ct = default)
        {
            limit = Math.Min(limit, 100);

            var userId = HttpContext.GetCurrentUserId();

            // Проверка доступа
            var isMember = await _db.ConversationMembers
                .AnyAsync(cm => cm.ConversationId == conversationId && cm.UserId == userId, ct);

            if (!isMember)
                return NotFound(new { error = new { code = "NOT_MEMBER", message = "Вы не участник чата" } });

            IQueryable<Message> query = _db.Messages
                .Where(m => m.ConversationId == conversationId && !m.IsDeleted);

            if (before.HasValue)
            {
                query = query.Where(m => m.Id < before.Value).OrderByDescending(m => m.Id);
            }
            else if (after.HasValue)
            {
                query = query.Where(m => m.Id > after.Value).OrderBy(m => m.Id);
            }
            else
            {
                query = query.OrderByDescending(m => m.Id);
            }

            var messages = await query
                .Take(limit + 1)
                .Select(m => new
                {
                    m.Id,
                    m.ConversationId,
                    m.SenderId,
                    m.Sender,
                    m.Type,
                    m.Text,
                    m.UpdatedAt,
                    m.IsDeleted,
                    m.CreatedAt,
                    m.ReplyToMessageId
                })
                .ToListAsync(ct);

            var replyToIds = messages
                .Where(m => m.ReplyToMessageId.HasValue)
                .Select(m => m.ReplyToMessageId.Value)
                .Distinct()
                .ToList();
            var replyMessages = new Dictionary<long, ReplyPreviewDto>();
            if (replyToIds.Any())
            {
                var replies = await _db.Messages
                    .Where(m => replyToIds.Contains(m.Id) && !m.IsDeleted)
                    .Include(m => m.Sender)
                    .Select(m => new ReplyPreviewDto
                    {
                        id = m.Id,
                        sender_id = m.SenderId,
                        sender_name = m.Sender.DisplayName,
                        text = m.Text.Length > 100 ? m.Text.Substring(0, 100) + "..." : m.Text,
                        type = m.Type.ToString().ToLower()
                    })
                    .ToListAsync(ct);

                replyMessages = replies.ToDictionary(r => r.id);
            }

            // Attachments
            var messageIds = messages.Select(m => m.Id).ToList();
            var attachments = new Dictionary<long, List<AttachmentDto>>();
            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            if (messageIds.Any())
            {
                var fileAttachments = await _db.FileAttachments
                    .Where(f => f.MessageId.HasValue && messageIds.Contains(f.MessageId.Value) && f.MessageId != 0)
                    .Select(f => new
                    {
                        f.MessageId,
                        Attachment = new AttachmentDto
                        {
                            id = f.Id,
                            file_name = f.FileName,
                            file_size = f.FileSize,
                            mime_type = f.MimeType,
                            url = $"{baseUrl}/api/files/{f.StoragePath}",
                            thumbnail_url = f.ThumbnailPath != null ? $"{baseUrl}/api/files/{f.ThumbnailPath}" : null
                        }
                    })
                    .ToListAsync(ct);

                attachments = fileAttachments
                    .GroupBy(f => f.MessageId.Value)
                    .ToDictionary(g => g.Key, g => g.Select(f => f.Attachment).ToList());
            }

            var hasMore = messages.Count > limit;

            // Обрезаем до нужного количества
            if (hasMore)
                messages = messages.Take(limit).ToList();

            // ReadCount + ReadBy
            var readCounts = await _db.MessageReadReceipts
                .Where(r => messageIds.Contains(r.MessageId))
                .GroupBy(r => r.MessageId)
                .Select(g => new { MessageId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.MessageId, g => g.Count, ct);
            var memberCount = await _db.ConversationMembers.CountAsync(cm => cm.ConversationId == conversationId, ct);
            var chatType = (await _db.Conversations.FirstOrDefaultAsync(x => x.Id == conversationId))?.Type ?? ConversationType.direct;
            var needFullReadBy = chatType == ConversationType.direct || memberCount <= 5;
            Dictionary<long, List<UserBriefDto>>? readByMap = null;
            if (needFullReadBy)
            {
                var readByData = await _db.MessageReadReceipts
                    .Where(r => messageIds.Contains(r.MessageId))
                    .Include(r => r.User)
                    .Select(r => new
                    {
                        r.MessageId,
                        User = new UserBriefDto
                        {
                            id = r.User.Id,
                            display_name = r.User.DisplayName,
                            avatar_url = r.User.AvatarUrl
                        }
                    })
                    .ToListAsync(ct);

                readByMap = readByData
                    .GroupBy(r => r.MessageId)
                    .ToDictionary(g => g.Key, g => g.Select(r => r.User).ToList());
            }

            var messageDtos = messages.Select(m => new MessageDto
            {
                id = m.Id,
                conversation_id = m.ConversationId,
                sender_id = m.SenderId,
                sender = new UserBriefDto
                {
                    id = m.Sender.Id,
                    display_name = m.Sender.DisplayName,
                    avatar_url = m.Sender.AvatarUrl
                },
                type = m.Type.ToString().ToLower(),
                text = m.Text,
                is_edited = m.UpdatedAt.HasValue,
                is_deleted = m.IsDeleted,
                created_at = m.CreatedAt,
                updated_at = m.UpdatedAt,
                attachments = attachments.GetValueOrDefault(m.Id) ?? new List<AttachmentDto>(),
                reply_to_id = m.ReplyToMessageId,
                reply_to = m.ReplyToMessageId.HasValue && replyMessages.ContainsKey(m.ReplyToMessageId.Value)
                    ? replyMessages[m.ReplyToMessageId.Value]
                    : null,
                read_count = readCounts.GetValueOrDefault(m.Id, 0),
                read_by = needFullReadBy && readByMap != null && readByMap.ContainsKey(m.Id)
                    ? readByMap[m.Id]
                    : null
            }).ToList();

            // Для режима before — возвращаем в правильном порядке (от старых к новым)
            if (before.HasValue)
            {
                messageDtos = messageDtos.OrderBy(m => m.id).ToList();
            }

            return Ok(new
            {
                messages = messageDtos,
                has_more = hasMore
            });
        }

        /// <summary>
        /// Получить список пользователей, прочитавших сообщение
        /// </summary>
        [HttpGet("messages/{messageId}/readby")]
        public async Task<IActionResult> GetMessageReadBy(
            long messageId,
            CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();

            // 1. Находим сообщение и проверяем доступ
            var message = await _db.Messages
                .FirstOrDefaultAsync(m => m.Id == messageId, ct);

            if (message == null)
                return NotFound(new { error = new { code = "MESSAGE_NOT_FOUND", message = "Сообщение не найдено" } });

            // 2. Проверяем, что пользователь участник чата
            var isMember = await _db.ConversationMembers
                .AnyAsync(cm => cm.ConversationId == message.ConversationId && cm.UserId == userId, ct);

            if (!isMember)
                return Forbid();

            // 3. Загружаем список прочитавших
            var readBy = await _db.MessageReadReceipts
                .Where(r => r.MessageId == messageId)
                .Include(r => r.User)
                .OrderBy(r => r.ReadAt)
                .Select(r => new UserBriefDto
                {
                    id = r.User.Id,
                    display_name = r.User.DisplayName,
                    avatar_url = r.User.AvatarUrl
                })
                .ToListAsync(ct);

            return Ok(new
            {
                message_id = messageId,
                read_count = readBy.Count,
                read_by = readBy
            });
        }


        /// <summary>
        /// Редактировать своё сообщение (только text)
        /// </summary>
        [HttpPatch("{id}/messages/{messageId}")]
        public async Task<IActionResult> EditMessage(Guid id, long messageId, [FromBody] EditMessageRequest request, CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();

            // 1. Находим сообщение
            var message = await _db.Messages.Include(m => m.Conversation).AsTracking()
                .FirstOrDefaultAsync(m => m.Id == messageId && m.ConversationId == id, ct);

            if (message == null)
                return NotFound(new { error = new { code = "MESSAGE_NOT_FOUND", message = "Сообщение не найдено" } });

            // 2. Только автор может редактировать
            if (message.SenderId != userId)
                return StatusCode(403, new { error = new { code = "NOT_OWN_MESSAGE", message = "Вы не автор сообщения" } });

            // 3. Только текстовые сообщения можно редактировать
            if (message.Type != MessageType.Text)
                return BadRequest(new { error = new { code = "INVALID_TYPE", message = "Можно редактировать только текстовые сообщения" } });

            // истёк лимит редактирования, >24ч
            if ((DateTime.Now - message.CreatedAt).TotalHours > 24)
                return StatusCode(422, new { error = new { code = "EDIT_TIME_EXPIRED", message = "Можно редактировать только в течение 24 часов" } });

            // 4. Обновляем
            message.Text = request.Text;
            message.UpdatedAt = DateTime.UtcNow;

            // 5. Обновляем денормализованное поле в Conversation
            if (message.Id == message.Conversation.LastMessageId)
            {
                var truncatedText = request.Text.Length > 100
                    ? request.Text[..100] + "..."
                    : request.Text;
                message.Conversation.LastMessageText = truncatedText;
                message.Conversation.UpdatedAt = message.UpdatedAt.Value;
            }

            await _db.SaveChangesAsync(ct);

            // 6. Инвалидируем кэш чата
            _cache.Invalidate(id);

            // 7. Уведомляем участников через хаб (опционально)
            await _hubContext.Clients.Group(id.ToString()).SendAsync("message_edited", new { 
                id = messageId, 
                conversation_id = id, 
                text = request.Text, 
                is_edited = true,
                updated_at = message.UpdatedAt });

            return Ok(new { message.Id, message.Text, message.UpdatedAt });
        }

        /// <summary>
        /// Удалить своё сообщение (soft delete)
        /// </summary>
        [HttpDelete("{id}/messages/{messageId}")]
        public async Task<IActionResult> DeleteMessage(Guid id, long messageId, CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();

            // 1. Находим сообщение
            var message = await _db.Messages.Include(m => m.Conversation).AsTracking()
                .FirstOrDefaultAsync(m => m.Id == messageId && m.ConversationId == id, ct);

            if (message == null)
                return NotFound(new { error = new { code = "MESSAGE_NOT_FOUND", message = "Сообщение не найдено" } });

            // 2. Только автор может удалить
            if (message.SenderId != userId)
                return StatusCode(403, new { error = new { code = "NOT_OWN_MESSAGE", message = "Вы не автор сообщения" } });

            // 3. Soft delete
            message.IsDeleted = true;
            message.Text = "[Сообщение удалено]";
            message.UpdatedAt = DateTime.UtcNow;

            // 4. Если это было последнее сообщение — обновляем LastMessage в Conversation
            if (message.Id == message.Conversation.LastMessageId)
            {
                // Найти предыдущее сообщение
                var prevMessage = await _db.Messages
                    .Where(m => m.ConversationId == id && !m.IsDeleted && m.Id < messageId)
                    .OrderByDescending(m => m.Id)
                    .FirstOrDefaultAsync(ct);

                if (prevMessage != null)
                {
                    message.Conversation.LastMessageId = prevMessage.Id;
                    message.Conversation.LastMessageText = prevMessage.Text.Length > 100
                        ? prevMessage.Text[..100] + "..."
                        : prevMessage.Text;
                    message.Conversation.LastMessageSenderId = prevMessage.SenderId;
                    message.Conversation.LastMessageCreatedAt = prevMessage.CreatedAt;
                }
                else
                {
                    message.Conversation.LastMessageId = null;
                    message.Conversation.LastMessageText = null;
                    message.Conversation.LastMessageSenderId = null;
                    message.Conversation.LastMessageCreatedAt = null;
                }
                message.Conversation.UpdatedAt = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync(ct);

            // 5. Инвалидируем кэш
            _cache.Invalidate(id);

            // 6. Уведомляем участников через хаб (опционально)
            await _hubContext.Clients.Group(id.ToString()).SendAsync("message_deleted", new
            {
                id = messageId,
                conversation_id = id
            });

            return NoContent();
        }

        /// <summary>
        /// Отметить все сообщения прочитанными вплоть до указанного
        /// </summary>
        [HttpPost("{id}/read")]
        public async Task<IActionResult> MarkAsRead(Guid id, [FromBody] MarkAsReadRequest request, CancellationToken ct = default)
        {
            try
            {
                var userId = HttpContext.GetCurrentUserId();

                // 1. Находим участника
                var member = await _db.ConversationMembers.AsTracking()
                    .FirstOrDefaultAsync(cm => cm.ConversationId == id && cm.UserId == userId, ct);

                if (member == null)
                    return NotFound(new { error = new { code = "NOT_MEMBER", message = "Вы не участник чата" } });

                // 2. Получаем последнее сообщение в чате (если не указано)
                var upToMessageId = request.last_read_message_id;
                if (upToMessageId == null)
                {
                    upToMessageId = await _db.Messages
                        .Where(m => m.ConversationId == id && !m.IsDeleted)
                        .OrderByDescending(m => m.Id)
                        .Select(m => (long?)m.Id)
                        .FirstOrDefaultAsync(ct);
                }

                if (upToMessageId == null)
                    return StatusCode(204, new { unread_count = 0 });

                // 3. Проверяем, что сообщение существует и в этом чате
                var messageExists = await _db.Messages
                    .AnyAsync(m => m.Id == upToMessageId && m.ConversationId == id, ct);

                if (!messageExists)
                    return NotFound(new { error = new { code = "MESSAGE_NOT_FOUND", message = "Сообщение не найдено в этом чате" } });

                var oldLastReadId = member.LastReadMessageId ?? 0;

                // 4. Если уже прочитано до этого сообщения или дальше - ничего не делаем
                if (oldLastReadId == upToMessageId)
                    return StatusCode(204, new { unread_count = member.UnreadCount });
                if (oldLastReadId > upToMessageId)
                {
                    // 5. Обновляем LastReadMessageId
                    member.LastReadMessageId = upToMessageId;

                    // 6. Находим сообщения, которые теперь стали НЕ прочитанными (чужие сообщения)
                    //var newlyReadMessages = await _db.Messages
                    //    .Where(m => m.ConversationId == id
                    //                && m.Id > upToMessageId
                    //                && m.Id <= oldLastReadId
                    //                && m.SenderId != userId)  // не свои сообщения
                    //    .Select(m => m.Id)
                    //    .ToListAsync(ct);

                    var toremove = await _db.MessageReadReceipts.Where(m => m.UserId == userId
                                    && m.MessageId > upToMessageId
                                    && m.MessageId <= oldLastReadId).ToListAsync(ct);
                    _db.MessageReadReceipts.RemoveRange(toremove);

                    // 8. Пересчитываем UnreadCount
                    var unreadCount = await _db.Messages
                        .CountAsync(m => m.ConversationId == id && m.Id > upToMessageId && !m.IsDeleted, ct);

                    member.UnreadCount = unreadCount;

                    await _db.SaveChangesAsync(ct);

                    // 9. Обновляем кэш
                    _cache.ResetUnreadCount(id, userId);

                    return StatusCode(204, new
                    {
                        unread_count = unreadCount
                    });
                }
                else
                {

                    // 5. Обновляем LastReadMessageId
                    member.LastReadMessageId = upToMessageId;

                    // 6. Находим сообщения, которые теперь стали прочитанными (чужие сообщения)
                    var newlyReadMessages = await _db.Messages
                        .Where(m => m.ConversationId == id
                                    && m.Id > oldLastReadId
                                    && m.Id <= upToMessageId
                                    && m.SenderId != userId)  // не свои сообщения
                        .Select(m => new { m.Id, m.SenderId })
                        .ToListAsync(ct);

                    // 7. Сохраняем факты прочтения (опционально, если нужна история)
                    if (newlyReadMessages.Any())
                    {
                        var receipts = newlyReadMessages.Select(m => new MessageReadReceipt
                        {
                            MessageId = m.Id,
                            UserId = userId,
                            ReadAt = DateTime.UtcNow
                        });
                        _db.MessageReadReceipts.AddRange(receipts);
                    }

                    // 8. Пересчитываем UnreadCount
                    var unreadCount = await _db.Messages
                        .CountAsync(m => m.ConversationId == id && m.Id > upToMessageId && !m.IsDeleted, ct);

                    member.UnreadCount = unreadCount;

                    await _db.SaveChangesAsync(ct);

                    // 9. Обновляем кэш
                    _cache.ResetUnreadCount(id, userId);

                    // 10. Уведомляем отправителей о прочтении
                    var bySender = newlyReadMessages.GroupBy(m => m.SenderId);

                    foreach (var senderGroup in bySender)
                    {
                        await _hubContext.Clients
                            .User(senderGroup.Key.ToString())
                            .SendAsync("message_read", new
                            {
                                conversation_id = id,
                                last_read_message_id = upToMessageId,
                                user_id = userId,
                                read_at = DateTime.UtcNow
                            });
                    }

                    return StatusCode(204, new
                    {
                        unread_count = unreadCount
                    });
                }
            }
            catch(Exception ex)
            {
                return StatusCode(204, new
                {
                    unread_count = 0,
                    error = ex.Message
                });
            }
        }
        #endregion

        #region Media
        [HttpGet("{id}/files")]
        public async Task<IActionResult> GetFiles(Guid id, [FromQuery] int limit = 50, [FromQuery] int offset = 0, CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();

            var files = await _db.FileAttachments
                .Where(f => f.ConversationId == id && f.Type == FileType.File)
                .OrderByDescending(f => f.CreatedAt)
                .Skip(offset)
                .Take(limit)
                .Select(f => new FileInfoResponse
                {
                    Id = f.Id,
                    FileName = f.FileName,
                    FileSize = f.FileSize,
                    MimeType = f.MimeType,
                    SenderId = f.UserId,
                    SenderName = f.User.DisplayName,
                    CreatedAt = f.CreatedAt,
                    Url = $"/api/files/{f.StoragePath}"
                })
                .ToListAsync(ct);

            var total = await _db.FileAttachments
                .CountAsync(f => f.ConversationId == id && f.Type == FileType.File, ct);

            return Ok(new { files, total });
        }

        [HttpGet("{id}/voice")]
        public async Task<IActionResult> GetVoiceMessages(Guid id, [FromQuery] int limit = 50, [FromQuery] int offset = 0, CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();

            var voiceMessages = await _db.FileAttachments
                .Where(f => f.ConversationId == id && f.Type == FileType.Voice)
                .OrderByDescending(f => f.CreatedAt)
                .Skip(offset)
                .Take(limit)
                .Select(f => new VoiceMessageResponse
                {
                    Id = f.Id,
                    MessageId = f.MessageId ?? 0,
                    SenderId = f.UserId,
                    SenderName = f.User.DisplayName,
                    Duration = f.Duration ?? 0, 
                    CreatedAt = f.CreatedAt,
                    Url = $"/api/files/{f.StoragePath}"
                })
                .ToListAsync(ct);

            return Ok(voiceMessages);
        }

        [HttpGet("{id}/links")]
        public async Task<IActionResult> GetLinks(Guid id, [FromQuery] int limit = 50, [FromQuery] int offset = 0, CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();

            // Извлекаем ссылки из текста сообщений
            var messages = await _db.Messages
                .Where(m => m.ConversationId == id && m.Text.Contains("http://") || m.Text.Contains("https://"))
                .OrderByDescending(m => m.CreatedAt)
                .Skip(offset)
                .Take(limit)
                .Select(m => new LinkResponse
                {
                    MessageId = m.Id,
                    Url = ExtractFirstUrl(m.Text),  // метод для извлечения
                    SenderId = m.SenderId,
                    SenderName = m.Sender.DisplayName,
                    CreatedAt = m.CreatedAt
                })
                .ToListAsync(ct);

            return Ok(messages);
        }

        /// <summary>
        /// Загрузить аватар группы (только для администратора)
        /// </summary>
        [HttpPost("{id}/avatar")]
        public async Task<IActionResult> UploadGroupAvatar(Guid id, IFormFile file, CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();

            // 1. Находим чат
            var conversation = await _db.Conversations.FirstOrDefaultAsync(c => c.Id == id, ct);

            if (conversation == null)
                return NotFound(new { error = new { code = "CONVERSATION_NOT_FOUND", message = "Диалог не найден" } });

            // 2. Только групповые чаты
            if (conversation.Type != ConversationType.group)
                return BadRequest(new { error = new { code = "NOT_GROUP", message = "Только групповые чаты могут иметь аватар" } });

            // 3. Проверка прав (только администратор)
            var isAdmin = await _db.ConversationMembers
                .AnyAsync(cm => cm.ConversationId == id && cm.UserId == userId && cm.IsAdmin, ct);

            if (!isAdmin)
                return StatusCode(403, new { error = new { code = "NOT_ADMIN", message = "Только администратор может изменять аватар группы" } });

            // 4. Проверка файла
            if (file == null || file.Length == 0)
                return UnprocessableEntity(new { error = new { code = "NO_FILE", message = "Файл не выбран" } });

            if (file.Length > 5 * 1024 * 1024)
                return UnprocessableEntity(new { error = new { code = "FILE_TOO_LARGE", message = "Файл превышает 5MB" } });

            var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();

            if (!allowedExtensions.Contains(extension))
                return UnprocessableEntity(new { error = new { code = "INVALID_FORMAT", message = "Поддерживаются только JPG, PNG, GIF, WEBP" } });

            // 5. Сохраняем файл
            var avatarsFolder = Path.Combine(_storageBasePath, "avatars", "conversations");
            Directory.CreateDirectory(avatarsFolder);

            // Удаляем старый аватар, если есть
            if (!string.IsNullOrEmpty(conversation.AvatarUrl))
            {
                var oldFilePath = Path.Combine(_storageBasePath, conversation.AvatarUrl.Replace($"{Request.Scheme}://{Request.Host}/api/files/", ""));
                _ = Task.Run(() => {
                    if (System.IO.File.Exists(oldFilePath))
                        try { System.IO.File.Delete(oldFilePath); } catch { }
                });
            }

            // Сохраняем новый
            var fileName = $"{id}_{DateTime.UtcNow.Ticks}{extension}";
            var filePath = Path.Combine(avatarsFolder, fileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream, ct);
            }

            // 6. Формируем URL
            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            var avatarUrl = $"{baseUrl}/api/files/avatars/conversations/{fileName}";

            // 7. Обновляем запись в БД
            conversation.AvatarUrl = avatarUrl;
            conversation.UpdatedAt = DateTime.UtcNow;
            _db.Conversations.Update(conversation);
            await _db.SaveChangesAsync(ct);

            // 8. Инвалидируем кэш
            _cache.Invalidate(id);

            // 9. Уведомляем всех участников чата через SignalR
            var updatedConversation = await BuildConversationResponse(id, userId, ct);
            await _hubContext.Clients.Group(id.ToString()).SendAsync("conversation_updated", updatedConversation);

            return Ok(new { avatar_url = avatarUrl });
        }
        #endregion

        #region DTOs

        public class FileInfoResponse
        {
            public Guid Id { get; set; }
            public string FileName { get; set; } = string.Empty;
            public long FileSize { get; set; }
            public string MimeType { get; set; } = string.Empty;
            public Guid SenderId { get; set; }
            public string SenderName { get; set; } = string.Empty;
            public DateTime CreatedAt { get; set; }
            public string Url { get; set; } = string.Empty;
            public string? ThumbnailUrl { get; set; }
        }

        public class VoiceMessageResponse
        {
            public Guid Id { get; set; }
            public long MessageId { get; set; }
            public Guid SenderId { get; set; }
            public string SenderName { get; set; } = string.Empty;
            public int Duration { get; set; }  // длительность в секундах
            public DateTime CreatedAt { get; set; }
            public string Url { get; set; } = string.Empty;
        }

        public class LinkResponse
        {
            public long MessageId { get; set; }
            public string Url { get; set; } = string.Empty;
            public string? Title { get; set; }  // можно позже добавить, вытаскивая <title> из HTML
            public string? Description { get; set; }
            public string? ImageUrl { get; set; }
            public Guid SenderId { get; set; }
            public string SenderName { get; set; } = string.Empty;
            public DateTime CreatedAt { get; set; }
        }

        public class MarkAsReadRequest
        {
            public long? last_read_message_id { get; set; }
        }

        public class PinRequest
        {
            [Required]
            public bool is_pinned { get; set; }
        }

        // Request DTO (можно поместить внутри контроллера или вынести в отдельный файл)
        public class UpdateConversationRequest
        {
            [MaxLength(100)]
            public string? Name { get; set; }

            [MaxLength(500)]
            public string? AvatarUrl { get; set; }
        }

        public class MuteRequest
        {
            [Required]
            public bool is_muted { get; set; }
        }

        public class AddMembersRequest
        {
            [Required]
            [MinLength(1)]
            public List<Guid> MemberIds { get; set; } = new();
        }

        // Request DTO
        public class CreateConversationRequest
        {
            /// <summary>
            /// [Direct, Group]
            /// </summary>
            [Required]
            public string Type { get; set; }

            [Required]
            public List<Guid> MemberIds { get; set; } = new();

            [MaxLength(100)]
            public string? Name { get; set; }

            [MaxLength(500)]
            public string? AvatarUrl { get; set; }
        }

        public class EditMessageRequest
        {
            [Required]
            [MaxLength(5000)]
            public string Text { get; set; } = string.Empty;
        }

        // Response DTOs
        public class ConversationsResponse
        {
            public List<ConversationResponse> Conversations { get; set; } = new();
            public int Total { get; set; }
        }

        public class ConversationResponse
        {
            public Guid id { get; set; }
            public string type { get; set; }
            public string? name { get; set; }
            public string? avatar_url { get; set; }
            public bool is_pinned { get; set; }
            public bool is_muted { get; set; }
            public List<MemberResponse> members { get; set; } = new();
            public LastMessageResponse? last_message { get; set; }
            public int unread_count { get; set; }
            public DateTime updated_at { get; set; }
        }

        public class MemberResponse
        {
            public string? custom_status;

            public Guid id { get; set; }
            public string display_name { get; set; } = string.Empty;
            public string? avatar_url { get; set; }
            public string? status { get; set; }
        }

        public class LastMessageResponse
        {
            public long id { get; set; }
            public string text { get; set; } = string.Empty;
            public string type { get; set; } = string.Empty;
            public Guid sender_id { get; set; }
            public DateTime created_at { get; set; }
        }

        #endregion
        
        // BuildConversationResponse (вспомогательный метод)
        private async Task<ConversationResponse> BuildConversationResponse(Guid conversationId, Guid userId, CancellationToken ct)
        {
            var data = await _db.ConversationMembers
                .Where(cm => cm.ConversationId == conversationId && cm.UserId == userId)
                .Select(cm => new
                {
                    cm.IsPinned,
                    cm.IsMuted,
                    cm.UnreadCount,
                    Conversation = cm.Conversation,
                    Members = cm.Conversation.Type == ConversationType.group
                        ? cm.Conversation.Members.Select(m => new MemberResponse
                        {
                            id = m.UserId,
                            display_name = m.User.DisplayName,
                            avatar_url = m.User.AvatarUrl,
                            status = m.User.IsOnline ? "online" : "offline", 
                            custom_status = m.User.CustomStatus
                        }).ToList()
                        : cm.Conversation.Members
                            .Where(m => m.UserId != userId)
                            .Select(m => new MemberResponse
                            {
                                id = m.UserId,
                                display_name = m.User.DisplayName,
                                avatar_url = m.User.AvatarUrl,
                                status = m.User.IsOnline ? "online" : "offline",
                                custom_status = m.User.CustomStatus
                            }).ToList(),
                    LastMessage = cm.Conversation.LastMessageId != null
                        ? new LastMessageResponse
                        {
                            id = cm.Conversation.LastMessage.Id,
                            text = cm.Conversation.LastMessage.Text.Length > 100
                                    ? cm.Conversation.LastMessage.Text.Substring(0, 100) + "..."
                                    : cm.Conversation.LastMessage.Text,
                            type = cm.Conversation.LastMessage.Type.ToString().ToLower(),
                            sender_id = cm.Conversation.LastMessage.SenderId,
                            created_at = cm.Conversation.LastMessage.CreatedAt
                        }
                        : null
                })
                .FirstOrDefaultAsync(ct);

            if (data == null)
                throw new NotFoundException("{\"error\": {\"code\": \"CONVERSATION_NOT_FOUND\", \"message\": \"Диалог не найден\"}}");
            //return NotFound(new { error = new { code = "CONVERSATION_NOT_FOUND", message = "Диалог не найден" } });

            return new ConversationResponse
            {
                id = conversationId,
                type = data.Conversation.Type.ToString(),
                name = data.Conversation.Type == ConversationType.direct ? data.Members?.FirstOrDefault()?.display_name : data.Conversation.Name,
                avatar_url = data.Conversation.AvatarUrl,
                is_pinned = data.IsPinned,
                is_muted = data.IsMuted,
                members = data.Members,
                last_message = data.LastMessage,
                unread_count = data.UnreadCount,
                updated_at = data.Conversation.UpdatedAt
            };
        }

        private static string ExtractFirstUrl(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var regex = new Regex(@"https?:\/\/(www\.)?[-a-zA-Z0-9@:%._\+~#=]{1,256}\.[a-zA-Z0-9()]{1,6}\b([-a-zA-Z0-9()@:%_\+.~#?&//=]*)");
            var match = regex.Match(text);
            return match.Success ? match.Value : string.Empty;
        }
    }
}
