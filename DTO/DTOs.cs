using IDMChat.Controllers;
using IDMChat.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace IDMChat.DTO
{

    public record ErrorDto
    {
        public string code { get; internal set; }
        public string message { get; internal set; }
    }

    #region Auth
    public class RefreshRequest
    {
        public string refresh_token { get; set; } = string.Empty;
    }

    public class LoginRequest
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public class LoginResultDto
    {
        public string access_token { get; set; }
        public string refresh_token { get; set; }
        public int expires_in { get; set; }
        public UserDto user { get; set; }
    }
    public class RefreshResultDto
    {
        public string access_token { get; set; }
        public int expires_in { get; set; }
    }
    #endregion


    public record PinDto
    {
        public bool is_pinned { get; internal set; }
    }
    public record MuteDto
    {
        public bool is_muted { get; internal set; }
    }
    public record AddMemberResult
    {
        public int added { get; internal set; }
        public List<Guid> member_ids { get; internal set; }
    }

    public record MessagesDto
    {
        public List<MessageDto> messages { get; internal set; }
        public bool has_more { get; internal set; }
    }
    public record MessageReadByDto
    {
        public long message_id { get; internal set; }
        public int read_count { get; internal set; }
        public List<UserBriefDto> read_by { get; internal set; }
    }

    /// <summary>
    /// Why not full message?
    /// </summary>
    public record EditMessageResult
    {
        public long id { get; internal set; }
        public string text { get; internal set; }
        public DateTime? updated_at { get; internal set; }
        public List<UserMention> mentions { get; set; } = new();
    }

    public record UnreadCountDto(int unread_count);

    public record UnreadCountErrorDto(int unread_count, string error);

    #region Medialinks in conversations
    public record MediaDto
    {
        public List<MediaInfoResponse> items { get; set; }
        public int total { get; set; }
    }
    public record FilesDto
    {
        public List<FileInfoResponse> items { get; set; }
        public int total { get; set; }
    }
    public record VoiceMessagesDto
    {
        public List<VoiceMessageResponse> items { get; set; }
        public int total { get; set; }
    }
    public record LinksDto
    {
        public List<LinkResponse> items { get; set; }
        public int total { get; set; }
    }

    public record UploadAvatarResult
    {
        public string? avatar_url { get; internal set; }
    }

    public class CommonItemResponce
    {
        public long message_id { get; set; }
        public Guid sender_id { get; set; }
        public string sender_name { get; set; } = string.Empty;
        public DateTime created_at { get; set; }
        public string url { get; set; } = string.Empty;

    }
    public class MediaInfoResponse: CommonItemResponce
    {
        public Guid id { get; set; }
        public string type { get; set; } = string.Empty; // "image" или "video"
        public int? duration { get; set; }
        public string? thumbnail_url { get; set; }
    }

    public class FileInfoResponse: CommonItemResponce
    {
        public Guid id { get; set; }
        public string file_name { get; set; } = string.Empty;
        public long file_size { get; set; }
        public string mime_type { get; set; } = string.Empty;
    }

    public class VoiceMessageResponse: CommonItemResponce
    {
        public Guid id { get; set; }
        public int duration { get; set; }  // длительность в секундах
    }

    public class LinkResponse: CommonItemResponce
    {
        //public string? title { get; set; }  // можно позже добавить, вытаскивая <title> из HTML
        //public string? description { get; set; }
        //public string? image_url { get; set; }
    }
    #endregion


    // Firebase (Соблюдаем snake_case фронтенда)
    public record RegisterTokenRequest(string token, string platform, string deviceId);
    public record DeleteTokenRequest(string deviceId);


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
        public string text { get; set; } = string.Empty;
        public List<UserMention> mentions { get; set; } = new();
    }

    // Response DTOs
    public class ConversationsResponse
    {
        public List<ConversationResponse> conversations { get; set; } = new();
        public int total { get; set; }
    }

    public class ConversationResponse
    {
        public Guid id { get; set; }
        public string type { get; set; }
        public string? name { get; set; }
        public string? avatar_url { get; set; }
        public List<MemberResponse> members { get; set; } = new();
        public LastMessageDto? last_message { get; set; }
        public DateTime updated_at { get; set; }

        // user-specific:
        public bool is_pinned { get; set; }
        public bool is_muted { get; set; }
        public int unread_count { get; set; }
    }

    public class MemberResponse
    {
        public string? custom_status { get; set; }

        public Guid id { get; set; }
        public string display_name { get; set; } = string.Empty;
        public string? avatar_url { get; set; }
        public string? role { get; set; }
        public string? status { get; set; }
        public bool is_online { get; set; }
        public DateTime? last_seen_at { get; set; }
        public DateTime joined_at { get; set; }
    }

    public class LastMessageDto
    {
        public LastMessageDto() { }
        public LastMessageDto(Conversation conversation)
        {
            id = conversation.LastMessage!.Id;
            text = conversation.LastMessage.Text.Length > 100
                ? conversation.LastMessage.Text.Substring(0, 100) + "..."
                : conversation.LastMessage.Text;
            type = conversation.LastMessage.Type.ToString().ToLower();
            sender_id = conversation.LastMessage.SenderId;
            created_at = conversation.LastMessage.CreatedAt;
        }
        public long id { get; set; }
        public string text { get; set; } = string.Empty;
        public string type { get; set; } = string.Empty;
        public Guid sender_id { get; set; }
        public string sender_name { get; internal set; } = string.Empty;
        public DateTime created_at { get; set; }
        public List<AttachmentDto> attachments { get; internal set; } = new();
        public List<UserMention> mentions { get; internal set; } = new();
    }

    public readonly record struct UserMention(Guid user_id, string display_name);

    public class MessageDto
    {
        public long id { get; set; }
        public Guid conversation_id { get; set; }

        public Guid sender_id { get; set; }
        public UserBriefDto sender { get; set; } = null!;

        public string type { get; set; } = string.Empty;
        public string? text { get; set; }
        public List<AttachmentDto>? attachments { get; set; }

        public ReplyPreviewDto? reply_to { get; set; }
        public long? reply_to_id { get; set; }

        public bool is_edited { get; set; }
        public bool is_deleted { get; set; }
        public DateTime created_at { get; set; }
        public DateTime? updated_at { get; set; }

        public int read_count { get; set; }

        public bool is_forwarded { get; set; }
        public UserBriefDto? forward_from { get; set; }

        // ⚠️ Для личных чатов, для групп от 5 человек - null
        public List<UserBriefDto>? read_by { get; set; }

        public List<UserMention> mentions { get; set; } = new List<UserMention>();

        public List<ReactionGroupDto> reactions { get; set; } = new();

        public bool is_pinned { get; set; }
        public Guid? pinned_by { get; set; }
        public DateTime? pinned_at { get; set; }
    }

    public record PinMessageRequestDto(long messageId);

    public class AttachmentDto
    {
        public Guid id { get; set; }
        public string file_name { get; set; } = string.Empty;
        public long file_size { get; set; }
        public string mime_type { get; set; } = string.Empty;
        public string url { get; set; } = string.Empty;
        public string? thumbnail_url { get; set; }
        public int? duration { get; set; }
        public FilesController.FileType type { get; set; }
        public List<double>? waveform { get; set; }
    }
    public class ConversationUpdatedDto
    {
        public Guid id { get; internal set; }
        public string type { get; internal set; }
        public string? name { get; internal set; }
        public string? avatar_url { get; internal set; }
        public DateTime? updated_at { get; internal set; }
        public LastMessageDto? last_message { get; internal set; }
    }
    public class ReplyPreviewDto
    {
        public long id { get; set; }
        public Guid sender_id { get; set; }
        public string sender_name { get; set; } = string.Empty;
        public string text { get; set; } = string.Empty;
        public string type { get; set; } = string.Empty; // text, image, etc.
        public List<AttachmentDto> attachments { get; set; } = new List<AttachmentDto>();
    }

    public class UserBriefDto
    {
        public Guid id { get; set; }
        public string display_name { get; set; } = string.Empty;
        public string? avatar_url { get; set; }
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
        public List<double>? waveform { get; set; }
    }
    public class UploadMultipleFilesResponse
    {
        public List<UploadFileResponse> files { get; set; } = new();
    }

    public record ForwardMessagesDto(
        List<long> MessageIds,       // Что пересылаем (оригинальные Message.Id)
        Guid TargetConversationId    // Куда пересылаем
    );

    public record HubForwardMessagesDto(
        Guid target_conversation_id,
        List<long> message_ids,
        List<string> temp_ids
    );

    public class ReactionGroupDto
    {
        public string emoji { get; set; } = string.Empty;
        public int count { get; set; }
        public List<Guid> userIds { get; set; } = new();
        public bool isMine { get; set; }
    }
    public record ReactionAddedResponseDto(
        string emoji,
        Guid userId,
        int count
    );
    public record AddReactionRequestDto(string emoji);


    public class ChangeRoleRequestDto
    {
        public string role { get; set; }
    }

    #region Folders
    public class ChatFolderDto
    {
        public Guid id { get; set; }
        public string title { get; set; } = string.Empty;
        public int position { get; set; }
        public List<Guid> conversation_ids { get; set; } = new(); // Обычные чаты папки
        public List<Guid> pinned_conversation_ids { get; set; } = new(); // Закрепленные чаты папки
        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }
    }

    public class ChatFoldersListResponseDto
    {
        public List<ChatFolderDto> folders { get; set; } = new();
    }

    public class CreateChatFolderRequestDto
    {
        public string title { get; set; } = string.Empty;

        // Массив чатов опционален, инициализируем пустым списком
        public List<Guid>? conversation_ids { get; set; } = new();
    }

    public class RenameChatFolderRequestDto
    {
        public string title { get; set; } = string.Empty;
    }

    public class ReorderChatFoldersRequestDto
    {
        public List<Guid> folder_ids { get; set; } = new();
    }

    public class AddChatsToFolderRequestDto
    {
        public List<Guid> conversation_ids { get; set; } = new();
    }

    public class PinChatsInFolderRequestDto
    {
        public List<Guid> conversation_ids { get; set; } = new();
    }
    #endregion

    #region Settings
    // Request DTO
    public class UpdateSettingsRequest
    {
        public bool? notifications_enabled { get; set; }
        public bool? sound_enabled { get; set; }

        [MaxLength(10)]
        public string? language { get; set; }

        [RegularExpression("^(system|light|dark)$", ErrorMessage = "Theme must be 'system', 'light', or 'dark'")]
        public string? theme { get; set; }
    }

    // Response DTO
    public class UserSettingsResponse
    {
        public bool notifications_enabled { get; set; }
        public bool sound_enabled { get; set; }
        public string language { get; set; } = "ru";
        public string theme { get; set; } = "system";
    }
    #endregion

    #region Profile

    public record ProfileDto
    {
        public Guid id { get; internal set; }
        public string username { get; internal set; }
        public string display_name { get; internal set; }
        public string? avatar_url { get; internal set; }
        public string phone { get; internal set; }
        public string email { get; internal set; }
        public string status { get; internal set; }
        public string? custom_status { get; internal set; }
        public bool is_online { get; internal set; }
        public DateTime last_seen_at { get; internal set; }
    }

    public record AvatarDto
    {
        public string? avatar_url { get; internal set; }
    }

    public class UpdateProfileRequest
    {
        public string? display_name { get; set; }
        public string? phone { get; set; }
        public string? email { get; set; }
        public UserPresenceStatus? status { get; set; }
        public string? custom_status { get; set; }
    }
    #endregion

    #region User
    public class UserDto
    {
        public Guid id { get; set; }
        public string username { get; set; } = string.Empty;
        public string display_name { get; set; } = string.Empty;
        public string? avatar_url { get; set; }
        public string? status { get; set; }
        public string? custom_status { get; set; }
        public bool is_online { get; set; }
        public DateTime last_seen_at { get; set; }
    }
    public record UsersDto
    {
        public int total { get; internal set; }
        public List<UserDto> users { get; internal set; }
    }

    #endregion

    #region IDM integration
    public class IdmVerifyRequestDto
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    // Пакет с данными, которые ИДМ вернет чату при успешной проверке
    public class IdmAuthResultDto
    {
        public int UserId { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;       // Роль пользователя в ИДМ ("Admin" / "User")
        public string CompanyCode { get; set; } = string.Empty; // Тот самый `idm` код компании
    }

    public class ExternalMessageRequestDto
    {
        public string chat_id { get; set; } = string.Empty; // Например, "tg_-100123456"
        public string text { get; set; } = string.Empty;             // Текст уведомления
    }
    #endregion
}
