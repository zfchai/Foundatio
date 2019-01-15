using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Lock;
using Foundatio.Queues;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Jobs {
    public abstract class QueueJobBase<T> : IQueueJob<T>, IHaveLogger where T : class {
        protected readonly ILogger _logger;
        protected readonly Lazy<IQueue<T>> _queue;
        protected readonly string _queueEntryName = typeof(T).Name;

        public QueueJobBase(Lazy<IQueue<T>> queue, ILoggerFactory loggerFactory = null) {
            _queue = queue;
            _logger = loggerFactory?.CreateLogger(GetType()) ?? NullLogger.Instance;
            AutoComplete = true;
        }

        public QueueJobBase(IQueue<T> queue, ILoggerFactory loggerFactory = null) : this(new Lazy<IQueue<T>>(() => queue), loggerFactory) {}

        protected bool AutoComplete { get; set; }
        public string JobId { get; } = Guid.NewGuid().ToString("N").Substring(0, 10);
        IQueue<T> IQueueJob<T>.Queue => _queue.Value;
        ILogger IHaveLogger.Logger => _logger;

        public async Task<JobResult> RunAsync(CancellationToken cancellationToken = default) {
            IQueueEntry<T> queueEntry;

            using (var timeoutCancellationTokenSource = new CancellationTokenSource(30000))
            using (var linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellationTokenSource.Token)) {
                try {
                    queueEntry = await _queue.Value.DequeueAsync(linkedCancellationToken.Token).AnyContext();
                } catch (Exception ex) {
                    return JobResult.FromException(ex, $"Error trying to dequeue message: {ex.Message}");
                }
            }

            return await ProcessAsync(queueEntry, cancellationToken).AnyContext();
        }

        public async Task<JobResult> ProcessAsync(IQueueEntry<T> queueEntry, CancellationToken cancellationToken) {
            if (queueEntry == null)
                return JobResult.Success;

            if (cancellationToken.IsCancellationRequested) {
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("Job was cancelled. Abandoning {QueueEntryName} queue entry: {Id}", _queueEntryName, queueEntry.Id);

                await queueEntry.AbandonAsync().AnyContext();
                return JobResult.CancelledWithMessage($"Abandoning {_queueEntryName} queue entry: {queueEntry.Id}");
            }

            var lockValue = await GetQueueEntryLockAsync(queueEntry, cancellationToken).AnyContext();
            if (lockValue == null) {
                await queueEntry.AbandonAsync().AnyContext();
                _logger.LogTrace("Unable to acquire queue entry lock.");
                return JobResult.Success;
            }

            bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
            try {
                LogProcessingQueueEntry(queueEntry);
                var result = await ProcessQueueEntryAsync(new QueueEntryContext<T>(queueEntry, lockValue, cancellationToken)).AnyContext();

                if (!AutoComplete || queueEntry.IsCompleted || queueEntry.IsAbandoned)
                    return result;

                if (result.IsSuccess) {
                    await queueEntry.CompleteAsync().AnyContext();
                    LogAutoCompletedQueueEntry(queueEntry);
                } else {
                    if (isTraceLogLevelEnabled)
                        _logger.LogTrace("Processing was not successful. Auto Abandoning {QueueEntryName} queue entry: {Id}", _queueEntryName, queueEntry.Id);
                    await queueEntry.AbandonAsync().AnyContext();
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning("Auto abandoned {QueueEntryName} queue entry: {Id}", _queueEntryName, queueEntry.Id);
                }

                return result;
            } catch (Exception ex) {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(ex, "Error processing {QueueEntryName} queue entry: {Id}", _queueEntryName, queueEntry.Id);

                if (!queueEntry.IsCompleted && !queueEntry.IsAbandoned)
                    await queueEntry.AbandonAsync().AnyContext();

                throw;
            } finally {
                if (isTraceLogLevelEnabled)
                    _logger.LogTrace("Releasing Lock for {QueueEntryName} queue entry: {Id}", _queueEntryName, queueEntry.Id);
                await lockValue.ReleaseAsync().AnyContext();
                if (isTraceLogLevelEnabled)
                    _logger.LogTrace("Released Lock for {QueueEntryName} queue entry: {Id}", _queueEntryName, queueEntry.Id);
            }
        }

        protected virtual void LogProcessingQueueEntry(IQueueEntry<T> queueEntry) {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Processing {QueueEntryName} queue entry: {Id}", _queueEntryName, queueEntry.Id);
        }

        protected virtual void LogAutoCompletedQueueEntry(IQueueEntry<T> queueEntry) {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Auto completed {QueueEntryName} queue entry: {Id}", _queueEntryName, queueEntry.Id);
        }

        protected abstract Task<JobResult> ProcessQueueEntryAsync(QueueEntryContext<T> context);

        protected virtual Task<ILock> GetQueueEntryLockAsync(IQueueEntry<T> queueEntry, CancellationToken cancellationToken = default) {
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("Returning Empty Lock for {QueueEntryName} queue entry: {Id}", _queueEntryName, queueEntry.Id);
            
            return Task.FromResult(Disposable.EmptyLock);
        }
    }
}