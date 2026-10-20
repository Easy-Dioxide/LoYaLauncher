namespace LoyaLauncher.Core;

/// <summary>
/// 进程内事件总线。用于跨模块广播下载进度、安装完成、游戏日志等事件，
/// 解耦 UI 与业务层，避免直接引用。
/// </summary>
public sealed class EventBus
{
    private static readonly Lazy<EventBus> _instance = new(() => new EventBus());
    public static EventBus Instance => _instance.Value;

    private readonly Lock _gate = new();
    private readonly Dictionary<Type, List<Delegate>> _handlers = new();

    /// <summary>订阅某类型事件，返回可释放的订阅句柄</summary>
    public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
    {
        lock (_gate)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var list))
            {
                list = [];
                _handlers[typeof(TEvent)] = list;
            }
            list.Add(handler);
        }
        return new Subscription(this, typeof(TEvent), handler);
    }

    /// <summary>发布事件，同步调用所有订阅者</summary>
    public void Publish<TEvent>(TEvent evt)
    {
        List<Delegate> snapshot;
        lock (_gate)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var list) || list.Count == 0)
                return;
            snapshot = [.. list];
        }
        foreach (var h in snapshot)
        {
            if (h is Action<TEvent> action)
            {
                try { action(evt); }
                catch { /* 单个订阅者异常不应影响其他订阅者 */ }
            }
        }
    }

    private void Unsubscribe(Type eventType, Delegate handler)
    {
        lock (_gate)
        {
            if (_handlers.TryGetValue(eventType, out var list))
            {
                list.Remove(handler);
                if (list.Count == 0)
                    _handlers.Remove(eventType);
            }
        }
    }

    private sealed class Subscription(EventBus bus, Type eventType, Delegate handler) : IDisposable
    {
        public void Dispose() => bus.Unsubscribe(eventType, handler);
    }
}
