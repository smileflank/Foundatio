﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Metrics;
using Foundatio.Serializer;
using Foundatio.Utility;
using Foundatio.Logging;

namespace Foundatio.Queues {
    public class InMemoryQueue<T> : IQueue<T> where T : class {
        private readonly ConcurrentQueue<QueueInfo<T>> _queue = new ConcurrentQueue<QueueInfo<T>>();
        private readonly ConcurrentDictionary<string, QueueInfo<T>> _dequeued = new ConcurrentDictionary<string, QueueInfo<T>>();
        private readonly ConcurrentQueue<QueueInfo<T>> _deadletterQueue = new ConcurrentQueue<QueueInfo<T>>();
        private readonly AutoResetEvent _autoEvent = new AutoResetEvent(false);
        private Action<QueueEntry<T>> _workerAction;
        private bool _workerAutoComplete;
        private readonly TimeSpan _workItemTimeout = TimeSpan.FromMinutes(10);
        private readonly TimeSpan _retryDelay = TimeSpan.FromMinutes(1);
        private readonly int[] _retryMultipliers = { 1, 3, 5, 10 };
        private readonly int _retries = 2;
        private int _enqueuedCount;
        private int _dequeuedCount;
        private int _completedCount;
        private int _abandonedCount;
        private int _workerErrorCount;
        private int _workerItemTimeoutCount;
        private CancellationTokenSource _workerCancellationTokenSource;
        private CancellationTokenSource _maintenanceCancellationTokenSource;
        private DateTime? _nextMaintenance = null;
        private readonly IMetricsClient _metrics;
        private readonly ISerializer _serializer;
        private IQueueEventHandler<T> _eventHandler = NullQueueEventHandler<T>.Instance; 

        public InMemoryQueue(int retries = 2, TimeSpan? retryDelay = null, int[] retryMultipliers = null, TimeSpan? workItemTimeout = null, IMetricsClient metrics = null, string statName = null, ISerializer serializer = null, IQueueEventHandler<T> eventHandler = null) {
            QueueId = Guid.NewGuid().ToString("N");
            _metrics = metrics;
            QueueSizeStatName = statName;
            _retries = retries;
            if (retryDelay.HasValue)
                _retryDelay = retryDelay.Value;
            if (retryMultipliers != null)
                _retryMultipliers = retryMultipliers;
            if (workItemTimeout.HasValue)
                _workItemTimeout = workItemTimeout.Value;

            _serializer = serializer ?? new JsonNetSerializer();

            if (eventHandler != null)
                _eventHandler = eventHandler;

            _maintenanceCancellationTokenSource = new CancellationTokenSource();
        }

        public long GetQueueCount() { return _queue.Count; }
        public long GetWorkingCount() { return _dequeued.Count; }
        public long GetDeadletterCount() { return _deadletterQueue.Count; }

        public long EnqueuedCount { get { return _enqueuedCount; } }
        public long DequeuedCount { get { return _dequeuedCount; } }
        public long CompletedCount { get { return _completedCount; } }
        public long AbandonedCount { get { return _abandonedCount; } }
        public long WorkerErrorCount { get { return _workerErrorCount; } }
        public long WorkItemTimeoutCount { get { return _workerItemTimeoutCount; } }
        public string QueueId { get; private set; }
        protected string QueueSizeStatName { get; set; }

        ISerializer IHaveSerializer.Serializer {
            get { return _serializer; }
        }

        public string Enqueue(T data) {
            string id = Guid.NewGuid().ToString("N");
            Logger.Trace().Message("Queue {0} enqueue item: {1}", typeof(T).Name, id).Write();
            if (!EventHandler.BeforeEnqueue(this, data))
                return null;

            var info = new QueueInfo<T> {
                Data = data.Copy(),
                Id = id
            };
            _queue.Enqueue(info);
            Logger.Trace().Message("Enqueue: Set Event").Write();
            _autoEvent.Set();
            Interlocked.Increment(ref _enqueuedCount);
            UpdateStats();

            EventHandler.AfterEnqueue(this, id, data);
            Logger.Trace().Message("Enqueue done").Write();

            return id;
        }

        public void StartWorking(Action<QueueEntry<T>> handler, bool autoComplete = false) {
            if (handler == null)
                throw new ArgumentNullException("handler");

            Logger.Trace().Message("Queue {0} start working", typeof(T).Name).Write();
            _workerAction = handler;
            _workerAutoComplete = autoComplete;
            if (_workerCancellationTokenSource != null)
                return;

            _workerCancellationTokenSource = new CancellationTokenSource();
            Task.Run(() => WorkerLoop(_workerCancellationTokenSource.Token));
        }

        public void StopWorking() {
            Logger.Trace().Message("Queue {0} stop working", typeof(T).Name).Write();
            if (_workerCancellationTokenSource != null)
                _workerCancellationTokenSource.Cancel();

            _workerCancellationTokenSource = null;
            _workerAction = null;
        }

        public QueueEntry<T> Dequeue(TimeSpan? timeout = null) {
            Logger.Trace().Message("Queue {0} dequeued item", typeof(T).Name).Write();
            if (!timeout.HasValue)
                timeout = TimeSpan.FromSeconds(30);

            Logger.Trace().Message("Queue count: {0}", _queue.Count).Write();
            if (_queue.Count == 0)
                _autoEvent.WaitOne(timeout.Value);
            if (_queue.Count == 0)
                return null;

            _autoEvent.Reset();
            Logger.Trace().Message("Dequeue: Attempt").Write();
            QueueInfo<T> info;
            if (!_queue.TryDequeue(out info) || info == null)
                return null;

            Logger.Trace().Message("Dequeue: Got Item").Write();
            EventHandler.OnDequeue(this, info.Id, info.Data);
            Interlocked.Increment(ref _dequeuedCount);
            ScheduleNextMaintenance(DateTime.UtcNow.Add(_workItemTimeout));

            info.Attempts++;
            info.TimeDequeued = DateTime.UtcNow;

            if (!_dequeued.TryAdd(info.Id, info))
                throw new ApplicationException("Unable to add item to the dequeued list.");

            UpdateStats();
            return new QueueEntry<T>(info.Id, info.Data.Copy(), this);
        }

        public void Complete(string id) {
            Logger.Trace().Message("Queue {0} complete item: {1}", typeof(T).Name, id).Write();
            EventHandler.OnComplete(this, id);

            QueueInfo<T> info = null;
            if (!_dequeued.TryRemove(id, out info) || info == null)
                throw new ApplicationException("Unable to remove item from the dequeued list.");

            Interlocked.Increment(ref _completedCount);
            UpdateStats();
            Logger.Trace().Message("Complete done: {0}", id).Write();
        }

        public void Abandon(string id) {
            Logger.Trace().Message("Queue {0} abandon item: {1}", typeof(T).Name, id).Write();

            EventHandler.OnAbandon(this, id);
            QueueInfo<T> info;
            if (!_dequeued.TryRemove(id, out info) || info == null)
                throw new ApplicationException("Unable to remove item from the dequeued list.");

            Interlocked.Increment(ref _abandonedCount);
            if (info.Attempts < _retries + 1) {
                if (_retryDelay > TimeSpan.Zero) {
                    Logger.Trace().Message("Adding item to wait list for future retry: {0}", id).Write();
                    Task.Factory.StartNewDelayed(GetRetryDelay(info.Attempts), () => Retry(info));
                } else {
                    Logger.Trace().Message("Adding item back to queue for retry: {0}", id).Write();
                    Retry(info);
                }
            } else {
                Logger.Trace().Message("Exceeded retry limit moving to deadletter: {0}", id).Write();
                _deadletterQueue.Enqueue(info);
            }
            UpdateStats();
            Logger.Trace().Message("Abondon complete: {0}", id).Write();
        }

        private void Retry(QueueInfo<T> info) {
            _queue.Enqueue(info);
            _autoEvent.Set();
        }

        private int GetRetryDelay(int attempts) {
            int maxMultiplier = _retryMultipliers.Length > 0 ? _retryMultipliers.Last() : 1;
            int multiplier = attempts <= _retryMultipliers.Length ? _retryMultipliers[attempts - 1] : maxMultiplier;
            return (int)(_retryDelay.TotalMilliseconds * multiplier);
        }

        public IEnumerable<T> GetDeadletterItems() {
            return _deadletterQueue.Select(i => i.Data);
        }

        public IQueueEventHandler<T> EventHandler
        {
            get { return _eventHandler; }
            set { _eventHandler = value ?? NullQueueEventHandler<T>.Instance; }
        }

        public void DeleteQueue() {
            Logger.Trace().Message("Deleting queue: {0}", typeof(T).Name).Write();
            _queue.Clear();
            _deadletterQueue.Clear();
            _dequeued.Clear();
            _enqueuedCount = 0;
            _dequeuedCount = 0;
            _completedCount = 0;
            _abandonedCount = 0;
            _workerErrorCount = 0;
            UpdateStats();
        }

        private Task WorkerLoop(CancellationToken token) {
            Logger.Trace().Message("WorkerLoop Start {0}", typeof(T).Name).Write();
            while (!token.IsCancellationRequested) {
                if (_queue.Count == 0 || _workerAction == null)
                    _autoEvent.WaitOne(TimeSpan.FromMilliseconds(250));

                Logger.Trace().Message("WorkerLoop Signaled {0}", typeof(T).Name).Write();
                QueueEntry<T> queueEntry = null;
                try {
                    queueEntry = Dequeue(TimeSpan.Zero);
                } catch (TimeoutException) { }

                if (queueEntry == null || _workerAction == null)
                    return TaskHelper.Completed();

                try {
                    _workerAction(queueEntry);
                    if (_workerAutoComplete)
                        queueEntry.Complete();
                } catch (Exception ex) {
                    Logger.Error().Exception(ex).Message("Worker error: {0}", ex.Message).Write();
                    queueEntry.Abandon();
                    Interlocked.Increment(ref _workerErrorCount);
                }
            }

            return TaskHelper.Completed();
        }

        private void UpdateStats() {
            if (_metrics != null && !String.IsNullOrEmpty(QueueSizeStatName))
                _metrics.Gauge(QueueSizeStatName, GetQueueCount());
        }

        private void ScheduleNextMaintenance(DateTime value)
        {
            Logger.Trace().Message("ScheduleNextMaintenance: value={0}", value).Write();
            if (value == DateTime.MaxValue)
                return;

            if (_nextMaintenance.HasValue && value > _nextMaintenance.Value)
                return;

            if (_maintenanceCancellationTokenSource != null)
                _maintenanceCancellationTokenSource.Cancel();
            _maintenanceCancellationTokenSource = new CancellationTokenSource();
            int delay = Math.Max((int)value.Subtract(DateTime.UtcNow).TotalMilliseconds, 0);
            _nextMaintenance = value;
            Logger.Trace().Message("Scheduling delayed task: delay={0}", delay).Write();
            Task.Factory.StartNewDelayed(delay, DoMaintenance, _maintenanceCancellationTokenSource.Token);
        }

        private void DoMaintenance() {
            Logger.Trace().Message("DoMaintenance {0}", typeof(T).Name).Write();

            DateTime minAbandonAt = DateTime.MaxValue;
            var now = DateTime.UtcNow;
            var abandonedKeys = new List<string>();
            foreach (string key in _dequeued.Keys)
            {
                var abandonAt = _dequeued[key].TimeDequeued.Add(_workItemTimeout);
                if (abandonAt < now)
                    abandonedKeys.Add(key);
                else if (abandonAt < minAbandonAt)
                    minAbandonAt = abandonAt;
            }

            ScheduleNextMaintenance(minAbandonAt);

            if (abandonedKeys.Count == 0)
                return;

            foreach (var key in abandonedKeys)
            {
                Logger.Info().Message("DoMaintenance Abandon: {0}", key).Write();
                Abandon(key);
                Interlocked.Increment(ref _workerItemTimeoutCount);
            }
        }

        public void Dispose() {
            Logger.Trace().Message("Queue {0} dispose", typeof(T).Name).Write();
            StopWorking();
            if (_maintenanceCancellationTokenSource != null)
                _maintenanceCancellationTokenSource.Cancel();
        }

        private class QueueInfo<TData> {
            public TData Data { get; set; }
            public string Id { get; set; }
            public int Attempts { get; set; }
            public DateTime TimeDequeued { get; set; }
        }
    }
}
