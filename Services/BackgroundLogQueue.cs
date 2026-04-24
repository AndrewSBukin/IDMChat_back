using IDMChat.Models;
using System.Threading.Channels;

namespace IDMChat.Services
{
    public interface IBackgroundLogQueue
    {
        void Enqueue(RequestResponseLog log);
        IAsyncEnumerable<RequestResponseLog> DequeueAllAsync(CancellationToken ct);
        long GetApproximateQueueSize();
        //bool TryGetChannelReader(out ChannelReader<RequestResponseLog> reader);
        void OnBatchConsumed(int batchSize);
    }

    public class BackgroundLogQueue : IBackgroundLogQueue, IDisposable
    {
        private readonly Channel<RequestResponseLog> _channel;
        private readonly ILogger<BackgroundLogQueue>? _logger;
        private long _approximateQueueSize;

        public BackgroundLogQueue(ILogger<BackgroundLogQueue>? logger = null)
        {
            _logger = logger;

            _channel = Channel.CreateUnbounded<RequestResponseLog>(new UnboundedChannelOptions
            {
                SingleReader = true,      // Only LogBatchProcessor reads
                SingleWriter = false,     // Multiple middleware instances can write
                AllowSynchronousContinuations = false  // Prevents thread pool starvation
            });

            // Monitor queue size periodically (for observability)
            if (_logger != null)
            {
                _ = MonitorQueueSizeAsync();
            }
        }

        public void Enqueue(RequestResponseLog log)
        {
            if (log == null) throw new ArgumentNullException(nameof(log));

            if (_channel.Writer.TryWrite(log))
            {
                Interlocked.Increment(ref _approximateQueueSize);
            }
            else
            {
                // Channel is closed (application shutting down)
                _logger?.LogWarning("Failed to enqueue log - channel is closed. Log: {RequestId}", log.RequestId);
            }
        }

        public IAsyncEnumerable<RequestResponseLog> DequeueAllAsync(CancellationToken cancellationToken)
        {
            return _channel.Reader.ReadAllAsync(cancellationToken);
        }

        public long GetApproximateQueueSize()
        {
            return Interlocked.Read(ref _approximateQueueSize);
        }

        public bool TryGetChannelReader(out ChannelReader<RequestResponseLog> reader)
        {
            reader = _channel.Reader;
            return true;
        }

        public void OnBatchConsumed(int batchSize)
        {
            Interlocked.Add(ref _approximateQueueSize, -batchSize);
            if (Interlocked.CompareExchange(ref _approximateQueueSize, 0, 0) < 0)
            {
                // Should never go negative, but reset if it does
                Interlocked.Exchange(ref _approximateQueueSize, 0);
            }
        }

        private async Task MonitorQueueSizeAsync()
        {
            var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));

            while (await timer.WaitForNextTickAsync())
            {
                var size = GetApproximateQueueSize();

                if (size > 10000)
                {
                    _logger?.LogWarning("Log queue backlog is growing: {QueueSize} logs pending", size);
                }
                else if (size > 1000)
                {
                    _logger?.LogDebug("Log queue size: {QueueSize}", size);
                }
            }
        }

        public void Dispose()
        {
            _channel.Writer.TryComplete();
        }
    }
}
