using IDMChat.Models;
using System.Threading.Channels;

namespace IDMChat.Services
{
    public interface IBackgroundPushQueue
    {
        void Enqueue(PushNotificationTask log);
        IAsyncEnumerable<PushNotificationTask> DequeueAllAsync(CancellationToken ct);
        long GetApproximateQueueSize();
        void OnBatchConsumed(int batchSize);
    }

    public class BackgroundPushQueue : IBackgroundPushQueue, IDisposable
    {
        private readonly Channel<PushNotificationTask> _channel;
        private readonly ILogger<BackgroundPushQueue>? _logger;
        private long _approximateQueueSize;

        public BackgroundPushQueue(ILogger<BackgroundPushQueue>? logger = null)
        {
            _logger = logger;

            _channel = Channel.CreateUnbounded<PushNotificationTask>(new UnboundedChannelOptions
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

        public void Enqueue(PushNotificationTask pushTask)
        {
            if (pushTask == null) throw new ArgumentNullException(nameof(pushTask));

            if (_channel.Writer.TryWrite(pushTask))
            {
                Interlocked.Increment(ref _approximateQueueSize);
            }
            else
            {
                // Channel is closed (application shutting down)
                _logger?.LogWarning("Failed to enqueue push notification - channel is closed. Sender: {SenderId}", pushTask.SenderId);
            }
        }

        public IAsyncEnumerable<PushNotificationTask> DequeueAllAsync(CancellationToken cancellationToken)
        {
            return _channel.Reader.ReadAllAsync(cancellationToken);
        }

        public long GetApproximateQueueSize()
        {
            return Interlocked.Read(ref _approximateQueueSize);
        }

        public bool TryGetChannelReader(out ChannelReader<PushNotificationTask> reader)
        {
            reader = _channel.Reader;
            return true;
        }
        /*
         {
  "username": "admin",
  "password": "Qq!11113"
}
         */
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

                if (size > 5000)
                {
                    _logger?.LogWarning("Push notification queue backlog is growing: {QueueSize} tasks pending", size);
                }
                else if (size > 500)
                {
                    _logger?.LogDebug("Push queue size: {QueueSize}", size);
                }
            }
        }

        public void Dispose()
        {
            _channel.Writer.TryComplete();
        }
    }
}
