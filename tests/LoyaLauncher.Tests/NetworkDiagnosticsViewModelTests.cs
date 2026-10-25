using System.Net;
using LoyaLauncher.App.ViewModels;
using LoyaLauncher.Core;
using LoyaLauncher.Downloader;

namespace LoyaLauncher.Tests;

/// <summary>
/// 网络诊断 ViewModel 测试（清单 §2.2/§9/§11）：
/// 全部请求经 mock HttpMessageHandler 拦截，零外网依赖；
/// 断言各源可达/不可达判定、自定义源注入、脱敏报告与近期日志联动。
/// </summary>
public sealed class NetworkDiagnosticsViewModelTests : IDisposable
{
    private readonly string _originalCustomMirror;

    public NetworkDiagnosticsViewModelTests()
    {
        NetLog.Reset();
        NetLog.FileSinkEnabled = false; // 测试不写用户目录日志
        _originalCustomMirror = ConfigStore.Instance.Config.CustomMirrorUrl;
        ConfigStore.Instance.Config.CustomMirrorUrl = "";
    }

    public void Dispose()
    {
        ConfigStore.Instance.Config.CustomMirrorUrl = _originalCustomMirror;
        NetLog.Reset();
        NetLog.FileSinkEnabled = true;
    }

    private static NetworkDiagnosticsViewModel CreateVm(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new RouteHandler(responder);
        var downloads = new DownloadManager(new EventBus(), handler: handler);
        return new NetworkDiagnosticsViewModel(downloads, ConfigStore.Instance);
    }

    /// <summary>全部静态源可达：HEAD 全部 200</summary>
    [Fact]
    public async Task RunAll_AllReachable_EveryTargetOk()
    {
        var vm = CreateVm(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await vm.RunAllCommand.ExecuteAsync(null);

        Assert.Equal(5, vm.Targets.Count); // 5 个静态源（未配置自定义源）
        Assert.All(vm.Targets, t => Assert.Equal(DiagState.Ok, t.State));
        Assert.All(vm.Targets, t => Assert.Equal(200, t.StatusCode));
        Assert.Contains("5 个可达", vm.StatusText);
        Assert.Contains("0 个不可达", vm.StatusText);
        Assert.True(vm.HasRecentLogs);
        Assert.Contains(vm.RecentLogs, l => l.Module == "diag-mojang");
    }

    /// <summary>单源故障：仅该源标记不可达，其余不受影响</summary>
    [Fact]
    public async Task RunAll_OneHostFails_OnlyThatTargetFailed()
    {
        var vm = CreateVm(req =>
            req.RequestUri?.Host == "bmclapi2.bangbang93.com"
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK));

        await vm.RunAllCommand.ExecuteAsync(null);

        var failed = vm.Targets.Where(t => t.State == DiagState.Failed).ToList();
        var ok = vm.Targets.Where(t => t.State == DiagState.Ok).ToList();
        Assert.Single(failed);
        Assert.Equal("BMCLAPI 镜像", failed[0].Name);
        Assert.Equal(500, failed[0].StatusCode);
        Assert.Contains("HTTP 500", failed[0].ErrorText);
        Assert.Equal(4, ok.Count);
        Assert.Contains("1 个不可达", vm.StatusText);
    }

    /// <summary>选中不可达源时暴露失败原因（错误横幅数据源）</summary>
    [Fact]
    public async Task SelectedTarget_Failed_ExposesErrorText()
    {
        var vm = CreateVm(req =>
            req.RequestUri?.Host == "api.github.com"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK));

        await vm.RunAllCommand.ExecuteAsync(null);

        var github = vm.Targets.Single(t => t.Name == "GitHub API（更新检查）");
        vm.SelectedTarget = github;
        Assert.Equal(DiagState.Failed, github.State);
        Assert.False(string.IsNullOrEmpty(vm.SelectedErrorText));
    }

    /// <summary>单源测试：只探测选中目标，其他保持待测试</summary>
    [Fact]
    public async Task TestOne_OnlyProbesSelectedTarget()
    {
        var vm = CreateVm(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var target = vm.Targets.Single(t => t.Name == "Mojang 版本清单");

        await vm.TestOneCommand.ExecuteAsync(target);

        Assert.Equal(DiagState.Ok, target.State);
        Assert.All(vm.Targets.Where(t => t != target), t => Assert.Equal(DiagState.Pending, t.State));
    }

    /// <summary>自定义源：配置后目标列表注入该源，探测走配置地址</summary>
    [Fact]
    public async Task CustomMirror_Configured_TargetIncludedAndProbed()
    {
        await using var server = new MockHttpServer();
        server.Handler = _ => new MockResponse(200, "application/json", "{}"u8.ToArray());
        ConfigStore.Instance.Config.CustomMirrorUrl = server.Prefix;

        var handler = new RouteHandler(req =>
            req.RequestUri?.Host == "127.0.0.1"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : new HttpResponseMessage(HttpStatusCode.OK));
        var downloads = new DownloadManager(new EventBus(), handler: handler);
        var vm = new NetworkDiagnosticsViewModel(downloads, ConfigStore.Instance);

        Assert.Equal(6, vm.Targets.Count);
        var custom = vm.Targets.Single(t => t.Name == "自定义下载源");
        Assert.Contains(server.Prefix, custom.Url);

        await vm.RunAllCommand.ExecuteAsync(null);
        Assert.Equal(DiagState.Ok, custom.State);
    }

    /// <summary>自定义源清空后目标列表回落为 5 个静态源</summary>
    [Fact]
    public void CustomMirror_Cleared_TargetRemoved()
    {
        ConfigStore.Instance.Config.CustomMirrorUrl = "http://127.0.0.1:9999";
        var vm = CreateVm(_ => new HttpResponseMessage(HttpStatusCode.OK));
        Assert.Equal(6, vm.Targets.Count);

        ConfigStore.Instance.Config.CustomMirrorUrl = "";
        vm.OnNavigatedTo(); // 进入页面时按最新配置重建
        Assert.Equal(5, vm.Targets.Count);
        Assert.DoesNotContain(vm.Targets, t => t.Name == "自定义下载源");
    }

    /// <summary>诊断报告脱敏：含目标与状态摘要，不含查询串/令牌</summary>
    [Fact]
    public async Task GenerateReport_Sanitized_NoQueryStrings()
    {
        var vm = CreateVm(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await vm.RunAllCommand.ExecuteAsync(null);
        var report = vm.GenerateReport();

        Assert.Contains("=== LoyaLauncher 网络诊断报告 ===", report);
        Assert.Contains("Mojang 版本清单", report);
        Assert.Contains("[OK", report);
        // Modrinth 探测地址带查询参数，脱敏后不得出现在报告中
        Assert.DoesNotContain("?query=", report);
        Assert.DoesNotContain("limit=1", report);
        Assert.Contains("api.modrinth.com/v2/search", report);
        // 近期请求（含探测记录）进入报告
        Assert.Contains("diag-mojang", report);
    }

    /// <summary>镜像链主机名写入报告（仅主机，无路径与查询）</summary>
    [Fact]
    public async Task GenerateReport_ContainsMirrorChainHosts()
    {
        var vm = CreateVm(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await vm.RunAllCommand.ExecuteAsync(null);
        var report = vm.GenerateReport();

        Assert.Contains("镜像链", report);
        Assert.Contains("bmclapi2.bangbang93.com", report);
    }

    /// <summary>近期日志列表反映 NetLog 内容（时间线联动）</summary>
    [Fact]
    public async Task RecentLogs_ReflectsNetLogEntries()
    {
        var vm = CreateVm(_ => new HttpResponseMessage(HttpStatusCode.OK));

        Assert.Empty(vm.RecentLogs);

        await vm.RunAllCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.RecentLogs);
        Assert.Contains(vm.RecentLogs, l => l.Host == "launchermeta.mojang.com");
        Assert.Contains(vm.RecentLogs, l => l.Status == "200");
    }

    /// <summary>URL 展示脱敏：查询串被剥离（DiagItem.DisplayUrl）</summary>
    [Fact]
    public void DisplayUrl_StripsQueryString()
    {
        var vm = CreateVm(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var modrinth = vm.Targets.Single(t => t.Name == "Modrinth API");

        Assert.DoesNotContain("?", modrinth.DisplayUrl);
        Assert.Contains("api.modrinth.com/v2/search", modrinth.DisplayUrl);
    }

    /// <summary>按 URL 路由的 mock 处理器（HEAD/GET 均可）</summary>
    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
