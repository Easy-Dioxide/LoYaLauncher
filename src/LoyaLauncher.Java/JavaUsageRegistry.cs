using System.Text.RegularExpressions;

namespace LoyaLauncher.Java;

/// <summary>
/// 托管 JRE 占用登记表。游戏进程运行期间通过 <see cref="Acquire"/> 持有对应主版本的租约，
/// 防止用户在游戏运行中删除正在使用的 Java 运行时（<see cref="JreManager.RemoveManagedRuntime"/> 会检查本表）。
/// 用法：using var lease = JavaUsageRegistry.Acquire(17);
/// </summary>
public static class JavaUsageRegistry
{
    // 主版本 -> 占用计数（支持多开：多个进程同时使用同一 Java）
    private static readonly Dictionary<int, int> _refCounts = new();
    private static readonly object _lock = new();

    /// <summary>该主版本的托管 JRE 是否正被占用</summary>
    public static bool IsInUse(int major)
    {
        lock (_lock) return _refCounts.TryGetValue(major, out var n) && n > 0;
    }

    /// <summary>登记占用，返回可释放的租约。同一主版本重复 Acquire 引用计数 +1</summary>
    public static JavaLease Acquire(int major)
    {
        lock (_lock)
        {
            _refCounts.TryGetValue(major, out var n);
            _refCounts[major] = n + 1;
        }
        return new JavaLease(major);
    }

    /// <summary>释放一次占用（引用计数 -1，归零才真正释放）</summary>
    public static void Release(int major)
    {
        lock (_lock)
        {
            if (!_refCounts.TryGetValue(major, out var n)) return;
            if (n <= 1) _refCounts.Remove(major);
            else _refCounts[major] = n - 1;
        }
    }

    /// <summary>占用租约，Dispose 时自动释放</summary>
    public sealed class JavaLease : IDisposable
    {
        private int _major;

        internal JavaLease(int major) => _major = major;

        public void Dispose()
        {
            var major = _major;
            if (major > 0)
            {
                _major = 0;
                Release(major);
            }
            GC.SuppressFinalize(this);
        }
    }
}
