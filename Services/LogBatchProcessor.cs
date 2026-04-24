using IDMChat.Models;
using IDMChat.Utils;
using System.Text.Json;

namespace IDMChat.Services
{
    public class LogBatchProcessor : BackgroundService
    {
        private readonly IBackgroundLogQueue _queue;
        private readonly ILogger<LogBatchProcessor> _logger;
        private readonly List<RequestResponseLog> _batch;
        private readonly TimeSpan _flushInterval;
        private readonly int _batchSize;
        private readonly string _logFilePath;

        public LogBatchProcessor( IBackgroundLogQueue queue, ILogger<LogBatchProcessor> logger, IConfiguration configuration)
        {
            try
            {
                _logger = logger;
                _queue = queue;

                logger.LogInformation("LogBatchProcessor constructor started");

                _flushInterval = TimeSpan.FromSeconds(configuration.GetValue("Logging:BatchFlushIntervalSeconds", 2));
                _batchSize = configuration.GetValue("Logging:BatchSize", 100);
                _batch = new List<RequestResponseLog>(_batchSize);

                _logFilePath = configuration.GetValue("Logging:FilePath", "logs/requests.jsonl");
                var directory = Path.GetDirectoryName(_logFilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                logger.LogInformation("LogBatchProcessor constructor completed. FlushInterval: {FlushInterval}, BatchSize: {BatchSize}, FilePath: {FilePath}",
                    _flushInterval, _batchSize, _logFilePath);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "LogBatchProcessor constructor failed");
                throw;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("LogBatchProcessor started. Flush interval: {FlushInterval}s, Batch size: {BatchSize}",
                _flushInterval.TotalSeconds, _batchSize);

            var enumerator = _queue.DequeueAllAsync(stoppingToken).GetAsyncEnumerator();

            try
            {
                var moveNextTask = enumerator.MoveNextAsync().AsTask();
                var lastFlushTime = DateTime.UtcNow;

                while (!stoppingToken.IsCancellationRequested)
                {
                    var timeUntilFlush = _flushInterval - (DateTime.UtcNow - lastFlushTime);
                    var timerDelay = timeUntilFlush > TimeSpan.Zero ? timeUntilFlush : TimeSpan.Zero;
                    var timerTask = Task.Delay(timerDelay, stoppingToken);

                    var completed = await Task.WhenAny(moveNextTask, timerTask);

                    if (completed == timerTask && !stoppingToken.IsCancellationRequested)
                    {
                        await timerTask; // Unwrap
                        if (_batch.Count > 0)
                        {
                            await FlushBatchAsync();
                            lastFlushTime = DateTime.UtcNow;
                        }
                        continue; // Don't process logs this iteration
                    }

                    if (completed == moveNextTask)
                    {
                        if (!await moveNextTask)
                        {
                            _logger.LogInformation("Log queue completed, shutting down");
                            break;
                        }
                        _batch.Add(enumerator.Current);

                        if (_batch.Count >= _batchSize)
                        {
                            await FlushBatchAsync();
                        }
                        moveNextTask = enumerator.MoveNextAsync().AsTask();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("LogBatchProcessor stopping");
            }
            finally
            {
                try
                {
                    await enumerator.DisposeAsync();
                }
                catch (NotSupportedException)
                {
                    // Известная проблема - игнорируем
                    _logger.LogDebug("DisposeAsync not supported - ignoring");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing enumerator");
                }
                await FlushBatchAsync();
            }
        }

        private async Task FlushBatchAsync()
        {
            if (_batch.Count == 0) return;

            var batchToWrite = _batch.ToList();
            _batch.Clear();

            try
            {
                var lines = new List<string>(batchToWrite.Count);
                foreach (var log in batchToWrite)
                {
                    var line = JsonSerializer.Serialize(log, JsonOptions.Default);
                    lines.Add(line);
                }

                // Batch write - one I/O operation
                await File.AppendAllLinesAsync(_logFilePath, lines);

                _queue.OnBatchConsumed(batchToWrite.Count);

                _logger.LogDebug("Flushed {Count} logs to {FilePath}", _batch.Count, _logFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to flush {Count} logs to {FilePath}", batchToWrite.Count, _logFilePath);
            }
        }

    }
}
