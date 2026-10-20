using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LoyaLauncher.Downloader;
using LoyaLauncher.Core;

namespace LoyaLauncher.App.ViewModels;

/// <summary>下载任务 UI 模型</summary>
public sealed partial class DownloadTaskItem : ObservableObject
{
    [ObservableProperty]
    private string _taskId = "";

    [ObservableProperty]
    private string _group = "";

    [ObservableProperty]
    private string _statusText = "等待中";

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private string _speedText = "";

    [ObservableProperty]
    private bool _isCompleted;
}

/// <summary>
/// 下载中心 ViewModel：实时展示所有下载任务的进度与速度。
/// </summary>
public sealed partial class DownloadCenterViewModel : ViewModelBase
{
    private readonly Core.EventBus _bus;
    private readonly Dictionary<string, DownloadTaskItem> _items = new();

    public DownloadCenterViewModel(Core.EventBus bus)
    {
        _bus = bus;
    }

    public ObservableCollection<DownloadTaskItem> Tasks { get; } = [];

    [ObservableProperty]
    private string _totalSpeedText = "0 B/s";

    [ObservableProperty]
    private bool _hasActiveTasks;

    [ObservableProperty]
    private int _activeCount;

    private readonly Dictionary<string, DownloadTaskItem> _active = new();

    public override void OnNavigatedTo()
    {
        _bus.Subscribe<DownloadProgressEvent>(OnProgress);
        _bus.Subscribe<DownloadCompletedEvent>(OnCompleted);
    }

    private void OnProgress(DownloadProgressEvent e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!_items.TryGetValue(e.TaskId, out var item))
            {
                item = new DownloadTaskItem { TaskId = e.TaskId, Group = e.Group };
                _items[e.TaskId] = item;
                Tasks.Add(item);
            }

            if (e.Group == "verify")
            {
                item.StatusText = "校验中";
                return;
            }

            if (e.TotalBytes > 0)
            {
                item.Percent = e.ReceivedBytes * 100.0 / e.TotalBytes;
                item.StatusText = $"{FormatBytes(e.ReceivedBytes)} / {FormatBytes(e.TotalBytes)}";
            }
            else
            {
                item.StatusText = FormatBytes(e.ReceivedBytes);
            }
            item.SpeedText = FormatSpeed(e.BytesPerSecond);

            _active[e.TaskId] = item;
            UpdateTotals();
        });
    }

    private void OnCompleted(DownloadCompletedEvent e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_items.TryGetValue(e.TaskId, out var item))
            {
                item.IsCompleted = e.Success;
                item.Percent = 100;
                item.SpeedText = "";
                item.StatusText = e.Success ? "完成" : $"失败: {e.Error}";
            }
            _active.Remove(e.TaskId);
            UpdateTotals();
        });
    }

    private void UpdateTotals()
    {
        ActiveCount = _active.Count;
        HasActiveTasks = _active.Count > 0;
        var speed = _active.Values.Sum(i => i.SpeedText.Length > 0 ? 0 : 0); // 速度已逐项显示
        TotalSpeedText = $"{_active.Count} 个任务进行中";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.##} {units[unit]}";
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "";
        return $"{FormatBytes((long)bytesPerSecond)}/s";
    }
}
