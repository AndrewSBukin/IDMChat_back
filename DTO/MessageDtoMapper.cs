using IDMChat.Models;
using IDMChat.Services;
using IDMChat.Utils;
using System.Text.Json;

namespace IDMChat.DTO
{

    public interface IMessageDtoMapper
    {
        MessageDto MapToDto(Message message, Guid currentUserId);
        List<MessageDto> MapToDtoList(IEnumerable<Message> messages, Guid currentUserId, Dictionary<long, int> readCounts, Dictionary<long, List<UserBriefDto>>? readByMap);
    }

    public class MessageDtoMapper : IMessageDtoMapper
    {
        private readonly UserCache _userCache;
        private readonly IChatPathUrlResolver _urlResolver;
        private readonly ChatDbContext _context; // Нужен для вытаскивания read_by и реакций, если они не были загружены через Include

        public MessageDtoMapper(UserCache userCache, IChatPathUrlResolver urlResolver, ChatDbContext context)
        {
            _userCache = userCache;
            _urlResolver = urlResolver;
            _context = context;
        }

        public MessageDto MapToDto(Message message, Guid currentUserId)
        {
            return MapInternal(message, currentUserId, 0, new List<UserBriefDto>());
        }

        public List<MessageDto> MapToDtoList(IEnumerable<Message> messages, Guid currentUserId, Dictionary<long, int> readCounts, Dictionary<long, List<UserBriefDto>>? readByMap)
        {
            return messages.Select(m =>
            {
                // Вытаскиваем количество прочтений из вашего словаря (если нет — 0)
                int count = readCounts.TryGetValue(m.Id, out var c) ? c : 0;

                // Вытаскиваем список прочитавших пользователей (если нет или отключено по ТЗ — null или пустой список)
                List<UserBriefDto>? readBy = null;
                if (readByMap != null && readByMap.TryGetValue(m.Id, out var list))
                {
                    readBy = list;
                }

                return MapInternal(m, currentUserId, count, readBy);
            }).ToList();
        }

        private MessageDto MapInternal(Message message, Guid currentUserId, int readCount, List<UserBriefDto>? readBy)
        {
            var senderFromCache = _userCache.GetUser(message.SenderId);

            UserBriefDto? forwardFromDto = null;
            if (message.IsForwarded && message.OriginalSenderId.HasValue)
            {
                var origSender = _userCache.GetUser(message.OriginalSenderId.Value);
                forwardFromDto = new UserBriefDto
                {
                    id = message.OriginalSenderId.Value,
                    display_name = origSender?.DisplayName ?? "Удаленный пользователь",
                    avatar_url = _urlResolver.ResolveUrl(origSender?.AvatarUrl)
                };
            }

            var attachmentsDto = message.FileAttachments?.Select(att => new AttachmentDto
            {
                id = att.Id,
                file_name = att.FileName,
                file_size = att.FileSize,
                mime_type = att.MimeType,
                url = _urlResolver.ResolveUrl(att.StoragePath),
                thumbnail_url = _urlResolver.ResolveUrl(att.ThumbnailPath),
                duration = att.Duration,
                type = att.Type,
                waveform = !string.IsNullOrEmpty(att.WaveformJson)
                    ? JsonSerializer.Deserialize<List<double>>(att.WaveformJson)
                    : null
            }).ToList() ?? new List<AttachmentDto>();

            var reactionsDto = message.Reactions?
                .GroupBy(r => r.Emoji)
                .Select(g => new ReactionGroupDto
                {
                    emoji = g.Key,
                    count = g.Count(),
                    userIds = g.Select(r => r.UserId).ToList(),
                    isMine = g.Any(r => r.UserId == currentUserId)
                }).ToList() ?? new List<ReactionGroupDto>();

            var mentionsDto = message.Mentions?.Select(m => new UserMention
            {
                user_id = m.UserId,
                display_name = _userCache.GetUser(m.UserId)?.DisplayName ?? "-"
            }).ToList() ?? new List<UserMention>();

            return new MessageDto
            {
                id = message.Id,
                conversation_id = message.ConversationId,
                sender_id = message.SenderId,
                type = message.Type.ToString().ToLower(),
                text = message.Text,
                created_at = message.CreatedAt,
                updated_at = message.UpdatedAt,
                is_edited = message.UpdatedAt.HasValue,
                is_deleted = message.IsDeleted,

                attachments = attachmentsDto,
                reactions = reactionsDto,
                mentions = mentionsDto,

                // ИНТЕГРАЦИЯ ВАШЕЙ ЛОГИКИ ПРОЧТЕНИЙ
                read_count = readCount,
                read_by = readBy, // Прилетит массив для ЛС/маленьких групп, либо null для больших групп строго по вашему ТЗ

                reply_to_id = message.ReplyToMessageId,
                reply_to = message.ReplyToMessage != null ? new ReplyPreviewDto
                {
                    id = message.ReplyToMessage.Id,
                    text = message.ReplyToMessage.Text,
                    sender_name = _userCache.GetUser(message.ReplyToMessage.SenderId)?.DisplayName ?? "-"
                } : null,

                is_pinned = message.IsPinned,
                pinned_by = message.PinnedByUserId,
                pinned_at = message.PinnedAt,

                sender = new UserBriefDto
                {
                    id = message.SenderId,
                    display_name = senderFromCache?.DisplayName ?? "-",
                    avatar_url = _urlResolver.ResolveUrl(senderFromCache?.AvatarUrl)
                },

                is_forwarded = message.IsForwarded,
                forward_from = forwardFromDto
            };
        }
    }
}
