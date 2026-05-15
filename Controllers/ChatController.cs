using Asp.Versioning;
using IDMChat.Domain;
using IDMChat.Middleware;
using IDMChat.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

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

        public ConversationsController(ChatDbContext dbContext, ILogger<ConversationsController> logger)
        {
            _db = dbContext;
            _logger = logger;
        }


        [HttpGet]
        public async Task<ActionResult<ConversationsResponse>> GetConversations(
            [FromQuery] int limit = 50,
            [FromQuery] int offset = 0,
            CancellationToken ct = default)
        {
            var userId = HttpContext.GetCurrentUserId();

            // 1. Один запрос: все чаты + участники + последнее сообщение
            var conversationsData = await _db.ConversationMembers
                .Where(cm => cm.UserId == userId)
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
                    Members = cm.Conversation.Type == ConversationType.Group
                        ? cm.Conversation.Members.Select(m => new MemberResponse
                        {
                            Id = m.UserId,
                            DisplayName = m.User.DisplayName,
                            AvatarUrl = m.User.AvatarUrl,
                            Status = m.User.IsOnline ? "online" : "offline"
                        }).ToList()
                        : cm.Conversation.Members
                            .Where(m => m.UserId != userId)
                            .Select(m => new MemberResponse
                            {
                                Id = m.UserId,
                                DisplayName = m.User.DisplayName,
                                AvatarUrl = m.User.AvatarUrl,
                                Status = m.User.IsOnline ? "online" : "offline"
                            }).ToList(),

                    // Последнее сообщение (из денормализованного поля)
                    LastMessage = cm.Conversation.LastMessageId != null
                        ? new LastMessageResponse
                        {
                            Id = cm.Conversation.LastMessage!.Id,
                            Text = cm.Conversation.LastMessage.Text.Length > 100
                                ? cm.Conversation.LastMessage.Text.Substring(0, 100) + "..."
                                : cm.Conversation.LastMessage.Text,
                            Type = cm.Conversation.LastMessage.Type,
                            SenderId = cm.Conversation.LastMessage.SenderId,
                            CreatedAt = cm.Conversation.LastMessage.CreatedAt
                        }
                        : null
                })
                .ToListAsync(ct);

            // 2. Получить total (отдельный запрос, но легкий)
            var total = await _db.ConversationMembers
                .Where(cm => cm.UserId == userId)
                .CountAsync(ct);

            var conversations = conversationsData.Select(data => new ConversationResponse
            {
                Id = data.Conversation.Id,
                Type = data.Conversation.Type,
                Name = data.Conversation.Name,
                AvatarUrl = data.Conversation.AvatarUrl,
                IsPinned = data.IsPinned,
                IsMuted = data.IsMuted,
                Members = data.Members,
                LastMessage = data.LastMessage,
                UnreadCount = data.UnreadCount,
                UpdatedAt = data.Conversation.UpdatedAt
            }).ToList();

            return Ok(new ConversationsResponse
            {
                Conversations = conversations,
                Total = total
            });
        }

        [HttpGet("{conversationId}/messages")]
        public async Task<IActionResult> GetMessages(Guid conversationId, int count = 50)
        {
            var messages = await _db.Messages
                .Where(m => m.ConversationId == conversationId && !m.IsDeleted)
                .OrderByDescending(m => m.SentAt)
                .Take(count)
                .ToListAsync();

            return Ok(messages);
        }

        [HttpPost]
        public async Task<ActionResult<ConversationResponse>> CreateConversation([FromBody] CreateConversationRequest request, CancellationToken ct = default)
        {
            var userId = GetCurrentUserId();
            var currentUser = await _db.Users.FindAsync(new object[] { userId }, ct);

            // 1. Валидация
            if (request.Type == ConversationType.Direct)
            {
                if (request.MemberIds?.Count != 1)
                    return BadRequest(new { error = new { code = "DIRECT_REQUIRES_ONE_MEMBER", message = "Для создания чата необходим собеседник" } });

                // Проверка: чат уже существует?
                var existing = await _db.ConversationMembers
                    .Where(cm => cm.UserId == userId)
                    .Select(cm => cm.Conversation)
                    .Where(c => c.Type == ConversationType.Direct)
                    .SelectMany(c => c.Members)
                    .Where(cm => request.MemberIds.Contains(cm.UserId))
                    .AnyAsync(ct);

                if (existing)
                    return Conflict(new { error = "CONVERSATION_EXISTS", message = "Такая беседа уже сущствует" });

                // Проверка компании
                var targetUser = await _db.Users.FindAsync(new object[] { request.MemberIds[0] }, ct);

                if (currentUser.idm != targetUser.idm && currentUser.Role != UserRole.Admin)
                    return BadRequest(new { error = "CONVERSATION_EXISTS", message = "Такая беседа уже сущствует" });
            }
            else // Group
            {
                if (request.MemberIds?.Count < 2)
                    return BadRequest(new { error = "GROUP_REQUIRES_AT_LEAST_TWO_MEMBERS", message = "Для создания группы необходим собеседник" });

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
                        return Forbid();
                }
            }

            // 2. Создание чата
            var conversation = new Conversation
            {
                Id = Guid.NewGuid(),
                Type = request.Type,
                Name = request.Type == ConversationType.Group ? request.Name : null,
                AvatarUrl = request.AvatarUrl,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
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
                    IsAdmin = request.Type == ConversationType.Group && memberId == userId, // создатель - админ
                    IsPinned = false,
                    IsMuted = false,
                    UnreadCount = 0,
                    JoinedAt = DateTime.UtcNow,
                    LastReadMessageId = null
                });
            }

            // 4. Системное сообщение для группы
            if (request.Type == ConversationType.Group)
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

                conversation.LastMessageId = systemMessage.Id;
                conversation.LastMessageText = systemMessage.Text;
                conversation.LastMessageSenderId = userId;
                conversation.LastMessageCreatedAt = systemMessage.CreatedAt;

                _db.Messages.Add(systemMessage);
            }

            // 5. Сохранение
            _db.Conversations.Add(conversation);
            _db.ConversationMembers.AddRange(members);
            await _db.SaveChangesAsync(ct);

            // 6. Обновить кэш
            //_cache.InvalidateUserConversations(userId);
            //foreach (var memberId in allMemberIds)
            //{
            //    _cache.InvalidateUserConversations(memberId);
            //}

            // 7. Вернуть объект чата
            var response = await BuildConversationResponse(conversation.Id, userId, ct);
            return CreatedAtAction(nameof(GetConversation), new { id = conversation.Id }, response);
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<ConversationResponse>> GetConversation(Guid id, CancellationToken ct = default)
        {
            var userId = GetCurrentUserId();
            //var currentUser = await _db.Users.FindAsync(new object[] { userId }, ct);

            // Проверка: пользователь в чате?
            var isMember = await _db.ConversationMembers
                .AnyAsync(cm => cm.ConversationId == id && cm.UserId == userId, ct);

            if (!isMember)
                return NotFound();

            var response = await BuildConversationResponse(id, userId, ct);
            return Ok(response);
        }

        // Request DTO
        public class CreateConversationRequest
        {
            [Required]
            public ConversationType Type { get; set; }

            [Required]
            public List<Guid> MemberIds { get; set; } = new();

            [MaxLength(100)]
            public string? Name { get; set; }

            [MaxLength(500)]
            public string? AvatarUrl { get; set; }
        }

        // BuildConversationResponse (вспомогательный метод)
        private async Task<ActionResult<ConversationResponse>> BuildConversationResponse(Guid conversationId, Guid userId, CancellationToken ct)
        {
            var data = await _db.ConversationMembers
                .Where(cm => cm.ConversationId == conversationId && cm.UserId == userId)
                .Select(cm => new
                {
                    cm.IsPinned,
                    cm.IsMuted,
                    cm.UnreadCount,
                    Conversation = cm.Conversation,
                    Members = cm.Conversation.Type == ConversationType.Group
                        ? cm.Conversation.Members.Select(m => new MemberResponse
                        {
                            Id = m.UserId,
                            DisplayName = m.User.DisplayName,
                            AvatarUrl = m.User.AvatarUrl,
                            Status = m.User.IsOnline ? "online" : "offline", 
                            CustomStatus = m.User.CustomStatus
                        }).ToList()
                        : cm.Conversation.Members
                            .Where(m => m.UserId != userId)
                            .Select(m => new MemberResponse
                            {
                                Id = m.UserId,
                                DisplayName = m.User.DisplayName,
                                AvatarUrl = m.User.AvatarUrl,
                                Status = m.User.IsOnline ? "online" : "offline",
                                CustomStatus = m.User.CustomStatus
                            }).ToList(),
                    LastMessage = cm.Conversation.LastMessageId != null
                        ? new LastMessageResponse
                        {
                            Id = cm.Conversation.LastMessage.Id,
                            Text = cm.Conversation.LastMessage.Text.Substring(0, 100),
                            Type = cm.Conversation.LastMessage.Type,
                            SenderId = cm.Conversation.LastMessage.SenderId,
                            CreatedAt = cm.Conversation.LastMessage.CreatedAt
                        }
                        : null
                })
                .FirstOrDefaultAsync(ct);

            if (data == null) return NotFound(new { error = new { code = "CONVERSATION_NOT_FOUND", message = "Диалог не найден" } });

            return new ConversationResponse
            {
                Id = conversationId,
                Type = data.Conversation.Type,
                Name = data.Conversation.Name,
                AvatarUrl = data.Conversation.AvatarUrl,
                IsPinned = data.IsPinned,
                IsMuted = data.IsMuted,
                Members = data.Members,
                LastMessage = data.LastMessage,
                UnreadCount = data.UnreadCount,
                UpdatedAt = data.Conversation.UpdatedAt
            };
        }

        // Response DTOs
        public class ConversationsResponse
        {
            public List<ConversationResponse> Conversations { get; set; } = new();
            public int Total { get; set; }
        }

        public class ConversationResponse
        {
            public Guid Id { get; set; }
            public ConversationType Type { get; set; }
            public string? Name { get; set; }
            public string? AvatarUrl { get; set; }
            public bool IsPinned { get; set; }
            public bool IsMuted { get; set; }
            public List<MemberResponse> Members { get; set; } = new();
            public LastMessageResponse? LastMessage { get; set; }
            public int UnreadCount { get; set; }
            public DateTime UpdatedAt { get; set; }
        }

        public class MemberResponse
        {
            public string? CustomStatus;

            public Guid Id { get; set; }
            public string DisplayName { get; set; } = string.Empty;
            public string? AvatarUrl { get; set; }
            public string? Status { get; set; }
        }

        public class LastMessageResponse
        {
            public long Id { get; set; }
            public string Text { get; set; } = string.Empty;
            public MessageType Type { get; set; }
            public Guid SenderId { get; set; }
            public DateTime CreatedAt { get; set; }
        }
    }
}
