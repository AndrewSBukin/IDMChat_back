using System.Text.Json.Serialization;

namespace IDMChat.Models
{
    public record RequestResponseLog
    {
        [JsonPropertyName("requestId")]
        public required string RequestId { get; init; }

        [JsonPropertyName("method")]
        public required string Method { get; init; }

        [JsonPropertyName("path")]
        public required string Path { get; init; }

        [JsonPropertyName("query")]
        public string? QueryString { get; init; }

        [JsonPropertyName("reqBody")]
        public string? RequestBody { get; init; }

        [JsonPropertyName("status")]
        public required int ResponseStatusCode { get; init; }

        [JsonPropertyName("resBody")]
        public string? ResponseBody { get; init; }

        [JsonPropertyName("durationMs")]
        public required long DurationMs { get; init; }

        [JsonPropertyName("timestamp")]
        public required DateTime Timestamp { get; init; }

        [JsonPropertyName("userId")]
        public string? UserId { get; init; }

        [JsonPropertyName("clientIp")]
        public string? ClientIp { get; init; }

        [JsonIgnore]
        public bool IsError => ResponseStatusCode >= 400;
    }
}
