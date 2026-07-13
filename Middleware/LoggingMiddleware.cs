using IDMChat.Models;
using IDMChat.Services;
using System.Diagnostics;
using System.Security.Claims;
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
        private readonly IBackgroundPushQueue _logQueue; // our custom queue
        private readonly int _maxBodyLength;

        public LoggingMiddleware(
            RequestDelegate next,
            ILogger<LoggingMiddleware> logger,
            IBackgroundPushQueue logQueue,
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

                string userIdClaim = null;
                if (context.User.Identity?.IsAuthenticated == true)
                {
                    userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                }

                var userId = context.User?.FindFirst("sub")?.Value ?? context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                // Создаем структурированное событие лога для NLog
                var logEvent = new NLog.LogEventInfo(NLog.LogLevel.Info, typeof(LoggingMiddleware).FullName, $"API: {context.Request.Method} {context.Request.Path}");
                logEvent.Properties["RequestId"] = context.TraceIdentifier;
                logEvent.Properties["Method"] = context.Request.Method;
                logEvent.Properties["Path"] = context.Request.Path;
                logEvent.Properties["QueryString"] = context.Request.QueryString.ToString();
                logEvent.Properties["RequestBody"] = requestBody;
                logEvent.Properties["ResponseStatusCode"] = context.Response.StatusCode;
                logEvent.Properties["ResponseBody"] = responseBody;
                logEvent.Properties["DurationMs"] = stopwatch.ElapsedMilliseconds;
                logEvent.Properties["UserId"] = userId ?? "-";
                logEvent.Properties["UserIdClaim"] = userIdClaim ?? "-";
                logEvent.Properties["ClientIp"] = clientIp;

                NLog.LogManager.GetLogger(logEvent.LoggerName).Log(logEvent);
                //NLog.LogManager.GetLogger(_logger.Name).Log(logEvent);

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
