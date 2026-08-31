using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace DysonHarness;

public sealed class DysonMessageBus : IDisposable
{
    private readonly ConcurrentDictionary<(Type EventType, string ScopeKey), HandlerList> _subscriptions = new();
    private readonly ILogger<DysonMessageBus>? _logger;
    private int _disposed;

    public DysonMessageBus(ILogger<DysonMessageBus>? logger = null)
    {
        _logger = logger;
    }

    public VoidResult<string> Publish<TEvent>(string scopeKey, TEvent message)
        where TEvent : IDysonMessageBusEvent
    {
        var check = ValidatePublish(scopeKey, message);
        if (check.IsError)
            return check;

        // ponytail: ceiling = synchronous fan-out on the publisher thread, no backpressure, no ordering
        // guarantee across keys; upgrade path = per-key channel if a slow handler ever stalls a session loop.
        foreach (var handler in SnapshotHandlers(typeof(TEvent), scopeKey))
        {
            try
            {
                if (handler.Sync is not null)
                    handler.Sync(message);
                else if (handler.Async is not null)
                    Observe(handler.Async(message, CancellationToken.None));
            }
            catch (Exception ex)
            {
                LogHandlerFailure(ex, typeof(TEvent), scopeKey);
            }
        }

        return VoidResult<string>.Success;
    }

    public Task<VoidResult<string>> PublishAsync<TEvent>(
        string scopeKey, TEvent message, CancellationToken cancellationToken = default)
        where TEvent : IDysonMessageBusEvent
    {
        var check = ValidatePublish(scopeKey, message);
        if (check.IsError)
            return Task.FromResult(check);

        return PublishAsyncCore(scopeKey, message, cancellationToken);
    }

    public Result<IDisposable, string> Subscribe<TEvent>(string scopeKey, Action<TEvent> handler)
        where TEvent : IDysonMessageBusEvent
    {
        if (handler is null)
            return Result<IDisposable, string>.AsError("handler is required");

        return SubscribeCore<TEvent>(scopeKey, new HandlerEntry
        {
            Sync = msg => handler((TEvent)msg),
        });
    }

    public Result<IDisposable, string> Subscribe<TEvent>(
        string scopeKey, Func<TEvent, CancellationToken, Task> handler)
        where TEvent : IDysonMessageBusEvent
    {
        if (handler is null)
            return Result<IDisposable, string>.AsError("handler is required");

        return SubscribeCore<TEvent>(scopeKey, new HandlerEntry
        {
            Async = (msg, ct) => handler((TEvent)msg, ct),
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _subscriptions.Clear();
    }

    private async Task<VoidResult<string>> PublishAsyncCore<TEvent>(
        string scopeKey, TEvent message, CancellationToken cancellationToken)
        where TEvent : IDysonMessageBusEvent
    {
        // ponytail: ceiling = synchronous fan-out on the publisher thread, no backpressure, no ordering
        // guarantee across keys; upgrade path = per-key channel if a slow handler ever stalls a session loop.
        foreach (var handler in SnapshotHandlers(typeof(TEvent), scopeKey))
        {
            try
            {
                if (handler.Sync is not null)
                    handler.Sync(message);
                else if (handler.Async is not null)
                    await handler.Async(message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogHandlerFailure(ex, typeof(TEvent), scopeKey);
            }
        }

        return VoidResult<string>.Success;
    }

    private Result<IDisposable, string> SubscribeCore<TEvent>(string scopeKey, HandlerEntry entry)
        where TEvent : IDysonMessageBusEvent
    {
        if (IsDisposed)
            return Result<IDisposable, string>.AsError("bus is disposed");
        if (string.IsNullOrWhiteSpace(scopeKey))
            return Result<IDisposable, string>.AsError("scope key is required");

        var key = (typeof(TEvent), scopeKey);
        while (true)
        {
            if (IsDisposed)
                return Result<IDisposable, string>.AsError("bus is disposed");

            var list = _subscriptions.GetOrAdd(key, static _ => new HandlerList());
            lock (list.Gate)
            {
                if (list.Dead)
                {
                    _subscriptions.TryRemove(
                        new KeyValuePair<(Type EventType, string ScopeKey), HandlerList>(key, list));
                    continue;
                }

                var n = list.Snapshot.Length;
                var next = new HandlerEntry[n + 1];
                Array.Copy(list.Snapshot, next, n);
                next[n] = entry;
                list.Snapshot = next;
            }

            if (IsDisposed)
            {
                Remove(key, entry);
                return Result<IDisposable, string>.AsError("bus is disposed");
            }

            return Result<IDisposable, string>.AsValue(new SubscriptionToken(this, key, entry));
        }
    }

    private VoidResult<string> ValidatePublish<TEvent>(string scopeKey, TEvent message)
        where TEvent : IDysonMessageBusEvent
    {
        if (IsDisposed)
            return VoidResult<string>.AsError("bus is disposed");
        if (string.IsNullOrWhiteSpace(scopeKey))
            return VoidResult<string>.AsError("scope key is required");
        if (message is null)
            return VoidResult<string>.AsError("message is required");

        return VoidResult<string>.Success;
    }

    private HandlerEntry[] SnapshotHandlers(Type eventType, string scopeKey)
    {
        var exact = Snapshot(eventType, scopeKey);
        if (scopeKey == DysonBusScopes.Wildcard)
            return exact;

        var wildcard = Snapshot(eventType, DysonBusScopes.Wildcard);
        if (wildcard.Length == 0)
            return exact;
        if (exact.Length == 0)
            return wildcard;

        var combined = new HandlerEntry[exact.Length + wildcard.Length];
        Array.Copy(exact, combined, exact.Length);
        Array.Copy(wildcard, 0, combined, exact.Length, wildcard.Length);
        return combined;
    }

    private HandlerEntry[] Snapshot(Type eventType, string scopeKey)
    {
        if (!_subscriptions.TryGetValue((eventType, scopeKey), out var list))
            return [];

        lock (list.Gate)
            return list.Snapshot;
    }

    private void Remove((Type EventType, string ScopeKey) key, HandlerEntry entry)
    {
        if (!_subscriptions.TryGetValue(key, out var list))
            return;

        lock (list.Gate)
        {
            var idx = Array.IndexOf(list.Snapshot, entry);
            if (idx < 0)
                return;

            if (list.Snapshot.Length == 1)
            {
                list.Snapshot = [];
                list.Dead = true;
                _subscriptions.TryRemove(new KeyValuePair<(Type EventType, string ScopeKey), HandlerList>(key, list));
                return;
            }

            var next = new HandlerEntry[list.Snapshot.Length - 1];
            if (idx > 0)
                Array.Copy(list.Snapshot, 0, next, 0, idx);
            if (idx < list.Snapshot.Length - 1)
                Array.Copy(list.Snapshot, idx + 1, next, idx, list.Snapshot.Length - idx - 1);
            list.Snapshot = next;
        }
    }

    private void Observe(Task task)
    {
        if (task.IsCompleted)
        {
            if (task.IsFaulted)
                LogHandlerFailure(task.Exception!.GetBaseException(), eventType: null, scopeKey: null);
            return;
        }

        _ = ObserveAsync(task);
    }

    private async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogHandlerFailure(ex, eventType: null, scopeKey: null);
        }
    }

    private void LogHandlerFailure(Exception ex, Type? eventType, string? scopeKey)
    {
        _logger?.LogError(ex, "DysonMessageBus handler failed for {EventType} at {ScopeKey}.", eventType, scopeKey);
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private sealed class HandlerEntry
    {
        public Action<object>? Sync;
        public Func<object, CancellationToken, Task>? Async;
    }

    private sealed class HandlerList
    {
        public readonly object Gate = new();
        public HandlerEntry[] Snapshot = [];
        public bool Dead;
    }

    private sealed class SubscriptionToken : IDisposable
    {
        private readonly DysonMessageBus _bus;
        private readonly (Type EventType, string ScopeKey) _key;
        private readonly HandlerEntry _entry;
        private int _disposed;

        public SubscriptionToken(
            DysonMessageBus bus,
            (Type EventType, string ScopeKey) key,
            HandlerEntry entry)
        {
            _bus = bus;
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            if (_bus.IsDisposed)
                return;

            _bus.Remove(_key, _entry);
        }
    }
}
