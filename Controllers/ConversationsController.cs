using Asp.Versioning;
using IDMChat.Domain;
using IDMChat.DTO;
using IDMChat.Hubs;
using IDMChat.Middleware;
using IDMChat.Models;
using IDMChat.Services;
using IDMChat.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using System.Runtime.CompilerServices;
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
        private readonly ChatStateCache _chatCache;
        private readonly UserCache _userCache;
        private readonly IHubContext<ChatHub> _hubContext;
        private readonly IChatPathUrlResolver _urlResolver;
        private readonly string _storageBasePath;

        public ConversationsController(
            ChatDbContext dbContext, 
            ILogger<ConversationsController> logger,
            ChatStateCache cache, UserCache ucache,
            IHubContext<ChatHub> hubContext, 
            IConfiguration configuration,
            IChatPathUrlResolver urlResolver)
        {
            _db = dbContext;
            _logger = logger;
            _chatCache = cache;
            _userCache = ucache;
            _hubContext = hubContext;
            _storageBasePath = configuration["Storage:BasePath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "uploads");
            _urlResolver = urlResolver;
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
                            status = _userCache.IsOnline(m.UserId) ? "online" : "offline",
                            custom_status = m.User.CustomStatus,
                            is_online = _userCache.IsOnline(m.UserId), 
                            last_seen_at = m.User.LastSeenAt
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
                        ? new LastMessageDto
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
                conversations = conversations,
                total = total
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
                        ? new LastMessageDto
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
                conversations = conversations,
                total = total
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

            // TODO: сбросить кеш?
            // 6. Обновить кэш
            //_cache.InvalidateUserConversations(userId);
            //foreach (var memberId in allMemberIds)
            //{
            //    _cache.InvalidateUserConversations(memberId);
            //}

            // Уведомление участников (кроме себя)
            var conversationUpdatedDto = await BuildConversationUpdatedDto(conversation, ct);
            if (conversation.Type == ConversationType.direct)
                (conversationUpdatedDto.name, conversationUpdatedDto.avatar_url) = await GetUserDisplayNameAndAvatar(userId);
            var allMemberIdsStr = allMemberIds.Where(x => x != userId).Select(m => m.ToString()).ToList();
            await _hubContext.Clients.Users(allMemberIdsStr).SendAsync("conversation_new", conversationUpdatedDto, ct);

            return CreatedAtAction(nameof(GetConversation), new { id = conversation.Id }, conversationUpdatedDto);
            //return StatusCode(201, response);
        }

        async Task<string> GetConversationName(Conversation conversation, Guid userId)
        {
            string convName = conversation.Name;
            if (conversation.Type == ConversationType.direct)
            {
                var user2 = await _db.Users
                    .Where(u => u.Id == conversation.Members.First(x => x.UserId != userId).UserId)
                    .Select(u => u.DisplayName)
                    .FirstOrDefaultAsync();

                convName = user2??"Имя не найдено";
            }
            return convName;
        }

        async Task<(string, string)> GetUserDisplayNameAndAvatar(Guid userId)
        {
            var user2 = await _db.Users
                .Where(u => u.Id == userId)
                .Select(u => new { u.DisplayName , u.AvatarUrl})
                .FirstOrDefaultAsync();
            return (user2.DisplayName, user2.AvatarUrl);
        }
        async Task<ConversationUpdatedDto> BuildConversationUpdatedDto(Conversation conversation, CancellationToken ct = default)
        {
            var message = await _db.Messages.Include(m => m.Conversation).AsTracking()
                .FirstOrDefaultAsync(m => m.Id == conversation.LastMessageId && m.ConversationId == conversation.Id, ct);

            var attachments = await _db.FileAttachments
                .Where(a => a.MessageId == conversation.LastMessageId)
                .Select(a => new AttachmentDto
                {
                    id = a.Id,
                    file_name = a.FileName,
                    file_size = a.FileSize,
                    mime_type = a.MimeType,
                    url = _urlResolver.ResolveUrl(a.StoragePath),
                    thumbnail_url = _urlResolver.ResolveUrl(a.ThumbnailPath)
                })
                .ToListAsync(ct);

            var sender_name = "";
            if (conversation.LastMessageSenderId.HasValue)
            {
                var sender = _userCache.GetUser(conversation.LastMessageSenderId.Value);
                sender_name = sender?.DisplayName;
            }
            var lastMessagePreview = new LastMessageDto
            {
                id = message.Id,
                text = conversation.LastMessageText ?? "",
                type = message.Type.ToString().ToLower(),
                sender_id = message.SenderId,
                sender_name = sender_name ?? "ошибка получения имени  ",
                created_at = message.CreatedAt,
                attachments = attachments
            };

            var conversationUpdatedDto = new ConversationUpdatedDto
            {
                id = conversation.Id,
                type = conversation.Type.ToString().ToLower(),
                name = conversation.Name,
                avatar_url = conversation.AvatarUrl,
                last_message = lastMessagePreview,
                updated_at = message.UpdatedAt.Value
            };
            return conversationUpdatedDto;
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
            _chatCache.Invalidate(id);

            // 7. Возвращаем обновлённый объект чата
            var conversationUpdatedDto = await BuildConversationUpdatedDto(conversation, ct);

            // Отправляем всем участникам чата
            var allMemberIds = conversation.Members.Select(m => m.UserId.ToString()).ToList();
            if (conversation.Type == ConversationType.direct)
            {
                var otherUserId = await _db.ConversationMembers.Where(cm => cm.ConversationId == id).Select(x => x.UserId)
                .FirstOrDefaultAsync(u => u != userId, ct);

                (conversationUpdatedDto.name, conversationUpdatedDto.avatar_url) = await GetUserDisplayNameAndAvatar(otherUserId);
                await _hubContext.Clients.User(userId.ToString().ToLower())
                    .SendAsync("conversation_updated", conversationUpdatedDto, ct);

                (conversationUpdatedDto.name, conversationUpdatedDto.avatar_url) = await GetUserDisplayNameAndAvatar(userId);
                await _hubContext.Clients.User(otherUserId.ToString().ToLower())
                    .SendAsync("conversation_updated", conversationUpdatedDto, ct);
            }
            else
            {
                await _hubContext.Clients.Users(allMemberIds)
                    .SendAsync("conversation_updated", conversationUpdatedDto, ct);
            }

            return Ok(conversationUpdatedDto);
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
                _chatCache.Invalidate(id);

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
                _chatCache.Invalidate(id);

                return NoContent();
            }

            return BadRequest(new { error = new ErrorDto { code = "UNKNOWN_TYPE", message = "Неизвестный тип чата" } });
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

            await _hubContext.Clients.User(userId.ToString()).SendAsync("conversation_options_updated", new {
                id = id,
                is_muted = member.IsMuted,
                is_pinned = member.IsPinned
            });

            // Инвалидируем кэш (чтобы обновился порядок сортировки)
            _chatCache.Invalidate(id);

            return Ok(new PinDto { is_pinned = member.IsPinned });
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

            await _hubContext.Clients.User(userId.ToString()).SendAsync("conversation_options_updated", new
            {
                id = id,
                is_muted = member.IsMuted,
                is_pinned = member.IsPinned
            });

            // Обновляем кэш (чтобы при отправке сообщений проверять is_muted)
            _chatCache.UpdateMuteStatus(id, userId, request.is_muted);

            return Ok(new MuteDto { is_muted = member.IsMuted });
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
            var fullConversation = await BuildConversationUpdatedDto(conversation, ct);
            foreach (var newMemberId in newMemberIds)
            {
                await _hubContext.Clients.User(newMemberId.ToString()).SendAsync("conversation_new", fullConversation);
            }

            // Уведомить остальных участников
            await _hubContext.Clients.Group(id.ToString()).SendAsync("members_added", new { conversation_id = id, member_ids = newMemberIds, added_by = userId });

            // 11. Инвалидируем кэш
            _chatCache.Invalidate(id);

            return Ok(new AddMemberResult { added = newMemberIds.Count, member_ids = newMemberIds });
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

            await _hubContext.Clients.Group(id.ToString()).SendAsync("members_removed", new { conversation_id = id, member_ids = new[] { memberId }, removed_by = userId });

            // 9. Инвалидируем кэш
            _chatCache.Invalidate(id);

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

            var chat = await _chatCache.GetConversationAsync(conversationId);
            if (chat == null || !chat.IsMember(userId))
            {
                return NotFound(new { error = new { code = "NOT_MEMBER", message = "Вы не участник чата" } });
            }

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

            var messageIds = messages.Select(m => m.Id).ToList();
            var hasMore = messages.Count > limit;
            // Обрезаем до нужного количества
            if (hasMore)
                messages = messages.Take(limit).ToList();

            var mentionsMap = new Dictionary<long, List<UserMention>>();
            if (messageIds.Any())
            {
                var dbMentions = await _db.MessageMentions
                    .Where(mm => messageIds.Contains(mm.MessageId))
                    .Select(mm => new { mm.MessageId, mm.UserId })
                    .ToListAsync(ct);

                mentionsMap = dbMentions
                    .GroupBy(mm => mm.MessageId)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(mm => new UserMention
                        {
                            user_id = mm.UserId,
                            display_name = _userCache.GetDisplayName(mm.UserId)
                        }).ToList()
                    );
            }

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
                    .Select(m => new ReplyPreviewDto
                    {
                        id = m.Id,
                        sender_id = m.SenderId,
                        sender_name = _userCache.GetDisplayName(m.SenderId),
                        text = m.Text.Length > 100 ? m.Text.Substring(0, 100) + "..." : m.Text,
                        type = m.Type.ToString().ToLower(),
                        attachments = m.FileAttachments.Select(a => new AttachmentDto
                        {
                            id = a.Id,
                            file_name = a.FileName,
                            file_size = a.FileSize,
                            mime_type = a.MimeType,
                            url = _urlResolver.ResolveUrl(a.StoragePath),
                            thumbnail_url = _urlResolver.ResolveUrl(a.ThumbnailPath)
                        }).ToList()
                    })
                    .ToListAsync(ct);
                
                replyMessages = replies.ToDictionary(r => r.id);
            }

            // Attachments
            var attachments = new Dictionary<long, List<AttachmentDto>>();
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
                            url = _urlResolver.ResolveUrl(f.StoragePath),
                            thumbnail_url = _urlResolver.ResolveUrl(f.ThumbnailPath)
                        }
                    })
                    .ToListAsync(ct);

                attachments = fileAttachments
                    .GroupBy(f => f.MessageId.Value)
                    .ToDictionary(g => g.Key, g => g.Select(f => f.Attachment).ToList());
            }



            // ReadCount + ReadBy
            var readCounts = await _db.MessageReadReceipts
                .Where(r => messageIds.Contains(r.MessageId))
                .GroupBy(r => r.MessageId)
                .Select(g => new { MessageId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.MessageId, g => g.Count, ct);

            var memberCount = chat.Members.Count;
            var needFullReadBy = chat.Type == ConversationType.direct || memberCount <= 5;
            Dictionary<long, List<UserBriefDto>>? readByMap = null;
            if (needFullReadBy && messageIds.Any())
            {
                var readByData = await _db.MessageReadReceipts
                    .Where(r => messageIds.Contains(r.MessageId))
                    .Select(r => new
                    {
                        r.MessageId,
                        User = new UserBriefDto
                        {
                            id = r.UserId,
                            display_name = _userCache.GetDisplayName(r.UserId),
                            avatar_url = _urlResolver.ResolveUrl(_userCache.GetUser(r.UserId).AvatarUrl)
                        }
                    })
                    .ToListAsync(ct);

                readByMap = readByData
                    .GroupBy(r => r.MessageId)
                    .ToDictionary(
                        g => g.Key, 
                        g => g.Select(r => r.User).ToList());
            }

            var messageDtos = messages.Select(m => new MessageDto
            {
                id = m.Id,
                conversation_id = m.ConversationId,
                sender_id = m.SenderId,
                sender = new UserBriefDto
                {
                    id = m.SenderId,
                    display_name = _userCache.GetDisplayName(m.SenderId),
                    avatar_url = _urlResolver.ResolveUrl(_userCache.GetUser(m.SenderId).AvatarUrl)
                },
                type = m.Type.ToString().ToLower(),
                text = m.Text,
                is_edited = m.UpdatedAt.HasValue,
                is_deleted = m.IsDeleted,
                created_at = m.CreatedAt,
                updated_at = m.UpdatedAt,
                attachments = attachments.GetValueOrDefault(m.Id) ?? new List<AttachmentDto>(),
                reply_to_id = m.ReplyToMessageId,
                reply_to = m.ReplyToMessageId.HasValue && replyMessages.TryGetValue(m.ReplyToMessageId.Value, out var reply) ? reply : null,
                read_count = readCounts.GetValueOrDefault(m.Id, 0),
                read_by = needFullReadBy && readByMap != null && readByMap.ContainsKey(m.Id)
                    ? readByMap[m.Id]
                    : null,
                mentions = mentionsMap.GetValueOrDefault(m.Id) ?? new List<UserMention>()
            }).ToList();

            // Для режима before — возвращаем в правильном порядке (от старых к новым)
            if (before.HasValue)
            {
                messageDtos = messageDtos.OrderBy(m => m.id).ToList();
            }

            return Ok(new MessagesDto
            {
                messages = messageDtos,
                has_more = hasMore
            });
        }


        /// <summary>
        /// Получить список пользователей, прочитавших сообщение
        /// </summary>
        [HttpGet("messages/{messageId}/readby")]
        public async Task<IActionResult> GetMessageReadBy(long messageId, CancellationToken ct = default)
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

            return Ok(new MessageReadByDto
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
            var message = await _db.Messages.AsTracking().FirstOrDefaultAsync(m => m.Id == messageId && m.ConversationId == id, ct);
            if (message == null)
                return NotFound(new { error = new { code = "MESSAGE_NOT_FOUND", message = "Сообщение не найдено" } });

            // 2. Только автор может редактировать
            if (message.SenderId != userId)
                return StatusCode(403, new { error = new { code = "NOT_OWN_MESSAGE", message = "Вы не автор сообщения" } });

            // 3. Только текстовые сообщения можно редактировать
            if (message.Type != MessageType.Text)
                return BadRequest(new { error = new { code = "INVALID_TYPE", message = "Можно редактировать только текстовые сообщения" } });

            // истёк лимит редактирования, >24ч
            if ((DateTime.UtcNow - message.CreatedAt).TotalHours > 24)
                return StatusCode(422, new { error = new { code = "EDIT_TIME_EXPIRED", message = "Можно редактировать только в течение 24 часов" } });

            // 4. Обновляем
            message.Text = request.text;
            message.UpdatedAt = DateTime.UtcNow;

            var currentMentionsDto = new List<UserMention>();
            var chat = await _chatCache.GetConversationAsync(id);

            if (request.mentions != null)
            {
                // Валидируем: тегнуть можно только участников этого чата
                var validUserIds = request.mentions
                    .Where(mId => chat != null && chat.IsMember(mId.user_id))
                    .Distinct()
                    .ToList();

                // TODO заменить Delete-Insert на Upsert
                // Удаляем старые упоминания этого сообщения
                var oldMentions = await _db.MessageMentions.Where(mm => mm.MessageId == messageId).ToListAsync(ct);
                _db.MessageMentions.RemoveRange(oldMentions);

                // Пишем новые
                foreach (var m in validUserIds)
                {
                    _db.MessageMentions.Add(new MessageMention { MessageId = messageId, UserId = m.user_id, DisplayName = m.display_name });
                    currentMentionsDto.Add(new UserMention(m.user_id, m.display_name));
                }
            }

            var attachments = await _db.FileAttachments
                .Where(a => a.MessageId == messageId)
                .Select(a => new AttachmentDto
                {
                    id = a.Id,
                    file_name = a.FileName,
                    file_size = a.FileSize,
                    mime_type = a.MimeType,
                    url = _urlResolver.ResolveUrl(a.StoragePath),
                    thumbnail_url = _urlResolver.ResolveUrl(a.ThumbnailPath)
                })
                .ToListAsync(ct);


            // 5. Обновляем денормализованное поле в Conversation
            if (chat != null && message.Id == chat.LastMessageId)
            {
                var truncatedText = request.text.Length > 100 ? request.text[..100] + "..." : request.text;

                await _db.Conversations
                    .Where(c => c.Id == id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(c => c.LastMessageText, truncatedText)
                        .SetProperty(c => c.UpdatedAt, message.UpdatedAt.Value), ct);

                var lastMessagePreview = new LastMessageDto
                {
                    id = message.Id,
                    text = truncatedText,
                    type = message.Type.ToString().ToLower(),
                    sender_id = message.SenderId, 
                    sender_name = _userCache.GetDisplayName(userId),
                    created_at = message.CreatedAt,
                    attachments = attachments,
                    mentions = currentMentionsDto
                };

                var conversationUpdatedDto = new ConversationUpdatedDto
                {
                    id = chat.Id,
                    type = chat.Type.ToString().ToLower(),
                    name = chat.Name,
                    avatar_url = _urlResolver.ResolveUrl(chat.AvatarUrl) ?? "",
                    last_message = lastMessagePreview,
                    updated_at = message.UpdatedAt.Value
                };

                // Отправляем всем участникам чата
                var onlineMembers = _userCache.GetOnlineMembers(chat.Members);

                if (chat.Type == ConversationType.direct)
                {
                    var otherUserId = chat.Members.FirstOrDefault(mId => mId != userId);

                    conversationUpdatedDto.name = _userCache.GetDisplayName(otherUserId);
                    conversationUpdatedDto.avatar_url = _urlResolver.ResolveUrl(_userCache.GetUser(otherUserId).AvatarUrl) ?? "";
                    await _hubContext.Clients.User(userId.ToString().ToLower()).SendAsync("conversation_updated", conversationUpdatedDto, ct);

                    conversationUpdatedDto.name = _userCache.GetDisplayName(userId);
                    conversationUpdatedDto.avatar_url = _urlResolver.ResolveUrl(_userCache.GetUser(userId).AvatarUrl) ?? "";
                    await _hubContext.Clients.User(otherUserId.ToString().ToLower()).SendAsync("conversation_updated", conversationUpdatedDto, ct);
                }
                else
                {
                    var onlineUserStrings = onlineMembers.Select(mId => mId.ToString()).ToList();
                    await _hubContext.Clients.Users(onlineUserStrings).SendAsync("conversation_updated", conversationUpdatedDto, ct);
                }

                _chatCache.UpdateLastMessage(id, message, truncatedText);
            }

            await _db.SaveChangesAsync(ct);

            var senderName = _userCache.GetDisplayName(userId);
            var senderAvatar = _urlResolver.ResolveUrl(_userCache.GetUser(userId).AvatarUrl);
            // 7. Уведомляем участников через хаб
            await _hubContext.Clients.Group(id.ToString()).SendAsync("message_edited", new MessageDto{
                id = messageId,
                conversation_id = id,
                sender_id = userId,
                sender = new UserBriefDto { id = userId, display_name = senderName, avatar_url = senderAvatar },
                type = message.Type.ToString().ToLower(),
                text = request.text,
                is_edited = true,
                updated_at = message.UpdatedAt,
                created_at = message.CreatedAt,
                attachments = attachments,
                mentions = currentMentionsDto, 
                is_deleted = message.IsDeleted, 
                reply_to_id = message.ReplyToMessageId, 
                //read_by =
                //read_count =
                //reply_to = 
            });

            return Ok(new EditMessageResult { id = message.Id, text = message.Text, updated_at = message.UpdatedAt, mentions = currentMentionsDto });
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
            //message.Text = "[Сообщение удалено]";
            message.UpdatedAt = DateTime.UtcNow;

            // 4. Если это было последнее сообщение — обновляем LastMessage в Conversation
            if (message.Id == message.Conversation.LastMessageId)
            {
                // Найти предыдущее сообщение
                var prevMessage = await _db.Messages
                    .Where(m => m.ConversationId == id && !m.IsDeleted && m.Id < messageId)
                    .OrderByDescending(m => m.Id)
                    .FirstOrDefaultAsync(ct);

                var chat = message.Conversation;
                var allMemberIds = chat.Members.Select(m => m.UserId.ToString()).ToList();

                var conversationUpdatedDto = new ConversationUpdatedDto
                {
                    id = chat.Id,
                    type = chat.Type.ToString().ToLower(),
                    name = chat.Name,
                    avatar_url = chat.AvatarUrl ?? "",
                    last_message = null,
                    updated_at = message.UpdatedAt.Value
                };

                if (prevMessage != null)
                {
                    message.Conversation.LastMessageId = prevMessage.Id;
                    message.Conversation.LastMessageText = prevMessage.Text.Length > 100
                        ? prevMessage.Text[..100] + "..."
                        : prevMessage.Text;
                    message.Conversation.LastMessageSenderId = prevMessage.SenderId;
                    message.Conversation.LastMessageCreatedAt = prevMessage.CreatedAt;

                    var attachments = await _db.FileAttachments
                        .Where(a => a.MessageId == prevMessage.Id)
                        .Select(a => new AttachmentDto
                        {
                            id = a.Id,
                            file_name = a.FileName,
                            file_size = a.FileSize,
                            mime_type = a.MimeType,
                            url = _urlResolver.ResolveUrl(a.StoragePath),
                            thumbnail_url = _urlResolver.ResolveUrl(a.ThumbnailPath)
                        })
                        .ToListAsync(ct);
                    var lastMessagePreview = new LastMessageDto
                    {
                        id = prevMessage.Id,
                        text = message.Conversation.LastMessageText ?? "",
                        type = prevMessage.Type.ToString().ToLower(),
                        sender_id = prevMessage.SenderId,
                        sender_name = _userCache.GetUser(prevMessage.SenderId)?.DisplayName ?? "ошибка получения имени ",
                        created_at = prevMessage.CreatedAt,
                        attachments = attachments
                    };
                    conversationUpdatedDto.last_message = lastMessagePreview;
                }
                else
                {
                    message.Conversation.LastMessageId = null;
                    message.Conversation.LastMessageText = null;
                    message.Conversation.LastMessageSenderId = null;
                    message.Conversation.LastMessageCreatedAt = null;

                }
                if (message.Conversation.Type == ConversationType.direct)
                {
                    var otherUserId = await _db.ConversationMembers.Where(cm => cm.ConversationId == id).Select(x => x.UserId)
                    .FirstOrDefaultAsync(u => u != userId, ct);

                    (conversationUpdatedDto.name, conversationUpdatedDto.avatar_url) = await GetUserDisplayNameAndAvatar(otherUserId);
                    await _hubContext.Clients.User(userId.ToString().ToLower())
                        .SendAsync("conversation_updated", conversationUpdatedDto, ct);

                    (conversationUpdatedDto.name, conversationUpdatedDto.avatar_url) = await GetUserDisplayNameAndAvatar(userId);
                    await _hubContext.Clients.User(otherUserId.ToString().ToLower())
                        .SendAsync("conversation_updated", conversationUpdatedDto, ct);
                }
                else
                {
                    await _hubContext.Clients.Users(allMemberIds)
                        .SendAsync("conversation_updated", conversationUpdatedDto, ct);
                }
                message.Conversation.UpdatedAt = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync(ct);

            // 5. Инвалидируем кэш
            _chatCache.Invalidate(id);

            // 6. Уведомляем участников через хаб (опционально)
            await _hubContext.Clients.Group(id.ToString()).SendAsync("message_deleted", new
            {
                id = messageId,
                conversation_id = id
            });

            // Находим всех участников чата, у которых это сообщение ещё не прочитано
            var membersWithUnread = await _db.ConversationMembers
                .Where(cm => cm.ConversationId == message.Conversation.Id
                             && (cm.LastReadMessageId == null || cm.LastReadMessageId < messageId))
                .ToListAsync();

            foreach (var member in membersWithUnread)
            {
                member.UnreadCount--;  // уменьшаем счётчик
                member.UnreadCount = Math.Max(0, member.UnreadCount);  // не меньше 0
            }

            await _db.SaveChangesAsync();

            // Отправляем обновления
            foreach (var member in membersWithUnread)
            {
                await _hubContext.Clients.User(member.UserId.ToString())
                    .SendAsync("unread_count_updated", new
                    {
                        conversation_id = message.Conversation.Id,
                        unread_count = member.UnreadCount
                    });
            }

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
                    _chatCache.ResetUnreadCount(id, userId);

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
                    _chatCache.ResetUnreadCount(id, userId);

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

                    return StatusCode( 204, new UnreadCountDto(unreadCount) );
                }
            }
            catch(Exception ex)
            {
                return StatusCode( 204, new UnreadCountErrorDto(0, ex.Message) );
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

            return Ok(new FilesDto { files = files, total = total });
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
                var oldFilePath = Path.Combine(_storageBasePath, conversation.AvatarUrl);
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
            var avatarUrl = Path.Combine("avatars", "conversations", fileName);

            // 7. Обновляем запись в БД
            conversation.AvatarUrl = avatarUrl;
            conversation.UpdatedAt = DateTime.UtcNow;
            _db.Conversations.Update(conversation);
            await _db.SaveChangesAsync(ct);

            // 8. Инвалидируем кэш
            _chatCache.Invalidate(id);

            // 9. Возвращаем обновлённый объект чата
            var conversationUpdatedDto = await BuildConversationUpdatedDto(conversation, ct);

            // Отправляем всем участникам чата
            var allMemberIds = conversation.Members.Select(m => m.UserId.ToString()).ToList();
            await _hubContext.Clients.Users(allMemberIds)
                .SendAsync("conversation_updated", conversationUpdatedDto, ct);

            return Ok(new UploadAvatarResult { avatar_url = _urlResolver.ResolveUrl(avatarUrl) });
        }
        #endregion

        // BuildConversationResponse (вспомогательный метод)
        private async Task<ConversationResponse> BuildConversationResponse(Guid conversationId, Guid userId, CancellationToken ct)
        {
            // 1. Извлекаем данные о чате из БД. Запрос стал максимально легковесным.
            var data = await _db.ConversationMembers
                .Where(cm => cm.ConversationId == conversationId && cm.UserId == userId)
                .Select(cm => new
                {
                    cm.IsPinned,
                    cm.IsMuted,
                    cm.UnreadCount,
                    Conversation = cm.Conversation,

                    // Загружаем Mentions только для последнего сообщения, если оно существует
                    LastMessageMentions = cm.Conversation.LastMessageId != null
                        ? _db.MessageMentions
                            .Where(mm => mm.MessageId == cm.Conversation.LastMessageId)
                            .Select(mm => mm.UserId)
                            .ToList()
                        : new List<Guid>(),

                    // Оставляем в подзапросе только вложения последнего сообщения
                    LastMessageAttachments = cm.Conversation.LastMessageId != null
                        ? _db.FileAttachments
                            .Where(a => a.MessageId == cm.Conversation.LastMessageId)
                            .Select(a => new AttachmentDto
                            {
                                id = a.Id,
                                file_name = a.FileName,
                                file_size = a.FileSize,
                                mime_type = a.MimeType,
                                url = _urlResolver.ResolveUrl(a.StoragePath),
                                thumbnail_url = _urlResolver.ResolveUrl(a.ThumbnailPath)
                            })
                            .ToList()
                        : new List<AttachmentDto>()
                })
                .FirstOrDefaultAsync(ct);

            if (data == null)
                throw new NotFoundException("{\"error\": {\"code\": \"CONVERSATION_NOT_FOUND\", \"message\": \"Диалог не найден\"}}");

            // 2. СБОРКА СПИСКА УЧАСТНИКОВ ПОЛНОСТЬЮ ИЗ КЭША (Минус тяжелый запрос к Users)
            // Достаем структуру чата из вашего ChatStateCache
            var cachedChat = await _chatCache.GetConversationAsync(conversationId);

            var membersList = cachedChat.Members.Select(memberId => {
                var user = _userCache.GetUser(memberId);
                return new MemberResponse
                {
                    id = memberId,
                    display_name = user?.DisplayName ?? "Сотрудник",
                    avatar_url = _urlResolver.ResolveUrl(user?.AvatarUrl),
                    status = _userCache.IsOnline(memberId) ? "online" : "offline",
                    custom_status = user?.CustomStatus,
                    is_online = _userCache.IsOnline(memberId),
                    last_seen_at = user?.LastSeenAt ?? DateTime.MinValue
                };
            }).ToList();

            // 3. ИСПРАВЛЕНИЕ БАГА ИМЕНИ ДЛЯ DIRECT-ЧАТОВ
            string chatName = data.Conversation.Name;
            string? chatAvatar = _urlResolver.ResolveUrl(data.Conversation.AvatarUrl);

            if (data.Conversation.Type == ConversationType.direct)
            {
                // Находим собеседника (тот, чей ID не равен текущему пользователю)
                var interlocutorId = cachedChat.Members.FirstOrDefault(id => id != userId);
                var interlocutor = _userCache.GetUser(interlocutorId);

                if (interlocutor != null)
                {
                    chatName = interlocutor.DisplayName;
                    chatAvatar = _urlResolver.ResolveUrl(interlocutor.AvatarUrl);
                }
            }

            // 4. СБОРКА ПОСЛЕДНЕГО СООБЩЕНИЯ С СИНХРОННЫМИ МЕНШЕНАМИ
            LastMessageDto? lastMessageDto = null;
            if (data.Conversation.LastMessageId != null)
            {
                // Маппим упоминания на лету из нашего in-memory кэша пользователей
                var mentions = data.LastMessageMentions
                    .Select(mId => new UserMention(mId, _userCache.GetDisplayName(mId)))
                    .ToList();

                var senderId = data.Conversation.LastMessageSenderId ?? Guid.Empty;
                var rawText = data.Conversation.LastMessageText ?? string.Empty;

                lastMessageDto = new LastMessageDto
                {
                    id = data.Conversation.LastMessageId.Value,
                    text = rawText.Length > 100 ? rawText[..100] + "..." : rawText,
                    type = data.Conversation.LastMessage?.Type.ToString().ToLower() ?? "text",
                    sender_id = senderId,
                    sender_name = _userCache.GetDisplayName(senderId),
                    created_at = data.Conversation.LastMessageCreatedAt ?? DateTime.UtcNow,
                    attachments = data.LastMessageAttachments,
                    mentions = mentions
                };
            }

            // Возвращаем итоговый чистый ответ
            return new ConversationResponse
            {
                id = conversationId,
                type = data.Conversation.Type.ToString().ToLower(),
                name = chatName,
                avatar_url = chatAvatar ?? "",
                is_pinned = data.IsPinned,
                is_muted = data.IsMuted,
                members = membersList,
                last_message = lastMessageDto,
                unread_count = data.UnreadCount,
                updated_at = data.Conversation.UpdatedAt
            };
        }
        private async Task<ConversationResponse> BuildConversationResponse0(Guid conversationId, Guid userId, CancellationToken ct)
        {
            var data = await _db.ConversationMembers
                .Where(cm => cm.ConversationId == conversationId && cm.UserId == userId)
                .Select(cm => new
                {
                    cm.IsPinned,
                    cm.IsMuted,
                    cm.UnreadCount,
                    Conversation = cm.Conversation,
                    Members = cm.Conversation.Members.Select(m => new MemberResponse
                    {
                        id = m.UserId,
                        display_name = m.User.DisplayName,
                        avatar_url = m.User.AvatarUrl,
                        status = _userCache.IsOnline(m.UserId) ? "online" : "offline", 
                        custom_status = m.User.CustomStatus, 
                        is_online = _userCache.IsOnline(m.UserId), 
                        last_seen_at = m.User.LastSeenAt
                    }).ToList(),
                    LastMessage = cm.Conversation.LastMessageId != null
                        ? new LastMessageDto
                        {
                            id = cm.Conversation.LastMessage.Id,
                            text = cm.Conversation.LastMessage.Text.Length > 100
                                    ? cm.Conversation.LastMessage.Text.Substring(0, 100) + "..."
                                    : cm.Conversation.LastMessage.Text,
                            type = cm.Conversation.LastMessage.Type.ToString().ToLower(),
                            sender_id = cm.Conversation.LastMessage.SenderId,
                            created_at = cm.Conversation.LastMessage.CreatedAt, 
                            attachments = _db.FileAttachments
                                .Where(a => a.MessageId == cm.Conversation.LastMessageId)
                                .Select(a => new AttachmentDto
                                {
                                    id = a.Id,
                                    file_name = a.FileName,
                                    file_size = a.FileSize,
                                    mime_type = a.MimeType,
                                    url = _urlResolver.ResolveUrl(a.StoragePath),
                                    thumbnail_url = _urlResolver.ResolveUrl(a.ThumbnailPath)
                                })
                                .ToList()
                        }
                        : null
                })
                .FirstOrDefaultAsync(ct);

            if (data == null)
                throw new NotFoundException("{\"error\": {\"code\": \"CONVERSATION_NOT_FOUND\", \"message\": \"Диалог не найден\"}}");

            return new ConversationResponse
            {
                id = conversationId,
                type = data.Conversation.Type.ToString(),
                name = data.Conversation.Type == ConversationType.direct ? data.Members?.FirstOrDefault()?.display_name : data.Conversation.Name,
                avatar_url = data.Conversation.Type == ConversationType.direct ? data.Members?.FirstOrDefault()?.avatar_url : data.Conversation.AvatarUrl,
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
