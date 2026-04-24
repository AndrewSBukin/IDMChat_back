using IDMChat.Models;
using IDMChat.Services;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace IDMChat.Middleware
{
    public class LoggingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<LoggingMiddleware> _logger;
        private readonly IBackgroundLogQueue _logQueue; // our custom queue
        private readonly int _maxBodyLength;

        public LoggingMiddleware(
            RequestDelegate next,
            ILogger<LoggingMiddleware> logger,
            IBackgroundLogQueue logQueue,
            IConfiguration configuration)
        {
            _next = next;
            _logger = logger;
            _logQueue = logQueue;
            _maxBodyLength = configuration.GetValue("Logging:MaxBodyLength", 4096);
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var stopwatch = Stopwatch.StartNew();

            context.Request.EnableBuffering();

            var requestBody = await CaptureRequestBodyAsync(context);

            // 3. Replace response stream to capture response body
            var originalResponseBody = context.Response.Body;
            using var responseMemoryStream = new MemoryStream();
            context.Response.Body = responseMemoryStream;

            try
            {
                await _next(context);
                stopwatch.Stop();

                var responseBody = await CaptureResponseBodyAsync(context, responseMemoryStream);

                // 6. Enqueue structured log (non‑blocking)
                _logQueue.Enqueue(new RequestResponseLog
                {
                    RequestId = context.TraceIdentifier,
                    Method = context.Request.Method,
                    Path = context.Request.Path,
                    QueryString = context.Request.QueryString.ToString(),
                    RequestBody = requestBody,      // truncated/masked
                    ResponseStatusCode = context.Response.StatusCode,
                    ResponseBody = responseBody,    // truncated/masked
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Timestamp = DateTime.UtcNow,
                    UserId = context.User?.FindFirst("sub")?.Value,
                    ClientIp = context.Connection.RemoteIpAddress?.ToString()
                });

                // 7. Copy captured response back to original stream
                responseMemoryStream.Seek(0, SeekOrigin.Begin);
                await responseMemoryStream.CopyToAsync(originalResponseBody);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "Error processing request {Path}", context.Request.Path);
                throw;
            }
            finally
            {
                context.Response.Body = originalResponseBody;
            }
        }

        private async Task<string> CaptureRequestBodyAsync(HttpContext context)
        {
            if (!IsLoggableContentType(context.Request.ContentType))
                return "[binary/not logged]";

            context.Request.Body.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
            var body = await reader.ReadToEndAsync();
            context.Request.Body.Seek(0, SeekOrigin.Begin); // reset for pipeline

            return TruncateAndMask(body, context.Request.Path);
        }

        private async Task<string> CaptureResponseBodyAsync(HttpContext context, MemoryStream responseStream)
        {
            responseStream.Seek(0, SeekOrigin.Begin);
            var body = await new StreamReader(responseStream).ReadToEndAsync();
            responseStream.Seek(0, SeekOrigin.Begin);

            if (!IsLoggableContentType(context.Response.ContentType))
                return "[binary/not logged]";

            return TruncateAndMask(body, context.Request.Path);
        }

        private bool IsLoggableContentType(string contentType)
        {
            return contentType?.StartsWith("application/json") == true ||
                   contentType?.StartsWith("text/") == true ||
                   contentType?.StartsWith("application/x-www-form-urlencoded") == true;
        }

        private string TruncateAndMask(string body, string path)
        {
            if (string.IsNullOrEmpty(body)) return body;

            const int maxBodyLength = 4096;
            var truncated = body.Length > maxBodyLength;
            var trimmed = truncated ? body[..maxBodyLength] + "… [truncated]" : body;

            if (path.Contains("/login") || path.Contains("/token"))
            {
                trimmed = Regex.Replace(
                    trimmed, 
                    "\"(password|token|secret)\"\\s*:\\s*\"[^\"]*\"",
                    "\"$1\":\"***REDACTED***\"",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            return trimmed;
        }
    }

}
