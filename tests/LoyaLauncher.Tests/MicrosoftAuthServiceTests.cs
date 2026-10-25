using System.Net;
using System.Text;
using System.Text.Json;
using LoyaLauncher.Auth;

namespace LoyaLauncher.Tests;

/// <summary>
/// 微软登录链路测试（清单 §7/§11）：mock HttpMessageHandler 按 URL 路由，
/// 覆盖成功登录、轮询状态（挂起/减速/拒绝/过期）、XSTS 资格错误、
/// 未拥有游戏、网络中断、刷新令牌过期、静默续期与取消。
/// </summary>
public sealed class MicrosoftAuthServiceTests
{
    /// <summary>
    /// 按 URL 路由的 mock handler。device-code → token → xbox → xsts → mc-login → profile
    /// 全链路默认返回成功载荷；各测试按需覆盖单段行为。
    /// </summary>
    private sealed class RoutingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<Uri> SeenUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SeenUris.Add(request.RequestUri!);
            return Task.FromResult(responder(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object payload) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage RawJson(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string DeviceCodeJson = """
        {"device_code":"DC-SECRET-VALUE","user_code":"UC-SECRET-VALUE","verification_uri":"https://www.microsoft.com/link",
         "expires_in":900,"interval":1,"message":"go verify"}
        """;

    private const string TokenOkJson = """
        {"access_token":"MS-ACCESS-SECRET","refresh_token":"MS-REFRESH-SECRET","expires_in":3600,"token_type":"Bearer"}
        """;

    private const string XboxOkJson = """
        {"Token":"XBL-TOKEN","DisplayClaims":{"xui":[{"uhs":"UHS-VALUE"}]}}
        """;

    private const string XstsOkJson = """
        {"Token":"XSTS-TOKEN","DisplayClaims":{"xui":[{"uhs":"UHS-VALUE"}]}}
        """;

    private const string McLoginOkJson = """
        {"access_token":"MC-ACCESS-SECRET","expires_in":7200}
        """;

    private const string ProfileOkJson = """
        {"id":"069a79f444e94726a5befca90e38aaf5","name":"Steve"}
        """;

    /// <summary>默认全链路成功的 responder（token 端点可带自定义行为）</summary>
    private static HttpResponseMessage FullChainResponder(HttpRequestMessage req, Func<HttpResponseMessage>? tokenOverride = null)
    {
        var path = req.RequestUri!.AbsoluteUri;
        if (path.Contains("devicecode")) return RawJson(DeviceCodeJson);
        if (path.Contains("/token")) return tokenOverride?.Invoke() ?? RawJson(TokenOkJson);
        if (path.Contains("user.auth.xboxlive.com")) return RawJson(XboxOkJson);
        if (path.Contains("xsts.auth.xboxlive.com")) return RawJson(XstsOkJson);
        if (path.Contains("login_with_xbox")) return RawJson(McLoginOkJson);
        if (path.Contains("minecraft/profile")) return RawJson(ProfileOkJson);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static (MicrosoftAuthService Service, RoutingHandler Handler, List<string> Logs) CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new RoutingHandler(responder);
        var logs = new List<string>();
        return (new MicrosoftAuthService(handler, logs.Add), handler, logs);
    }

    // ======================= 成功路径 =======================

    [Fact]
    public async Task 完整登录链路成功_返回正版账户()
    {
        var (svc, _, _) = CreateService(req => FullChainResponder(req));

        var deviceCode = await svc.RequestDeviceCodeAsync();
        Assert.Equal("UC-SECRET-VALUE", deviceCode.UserCode);
        Assert.Equal(1, deviceCode.IntervalSeconds);
        Assert.Equal(900, deviceCode.ExpiresInSeconds);

        var account = await svc.PollAndCompleteAsync(deviceCode);

        Assert.Equal("Steve", account.PlayerName);
        // 32 位无横线 UUID 规范化为 8-4-4-4-12
        Assert.Equal("069a79f4-44e9-4726-a5be-fca90e38aaf5", account.PlayerUuid);
        Assert.Equal("MC-ACCESS-SECRET", account.AccessToken);
        Assert.Equal("MS-REFRESH-SECRET", account.RefreshToken);
        Assert.False(account.IsExpired);
    }

    [Fact]
    public async Task 阶段日志_只含阶段与错误码_不含任何令牌或设备码()
    {
        var (svc, _, logs) = CreateService(req => FullChainResponder(req));

        var deviceCode = await svc.RequestDeviceCodeAsync();
        await svc.PollAndCompleteAsync(deviceCode);

        Assert.NotEmpty(logs);
        Assert.Contains(logs, l => l.Contains("device-code") && l.Contains("ok"));
        Assert.Contains(logs, l => l.Contains("token-poll") && l.Contains("ok"));
        Assert.Contains(logs, l => l.Contains("xbox-auth") && l.Contains("ok"));
        Assert.Contains(logs, l => l.Contains("xsts-auth") && l.Contains("ok"));
        Assert.Contains(logs, l => l.Contains("mc-login") && l.Contains("ok"));
        Assert.Contains(logs, l => l.Contains("profile") && l.Contains("ok"));

        // 敏感值绝不出现在日志（清单 §7）
        foreach (var secret in new[] { "MS-ACCESS-SECRET", "MS-REFRESH-SECRET", "MC-ACCESS-SECRET",
                                       "XBL-TOKEN", "XSTS-TOKEN", "DC-SECRET-VALUE", "UC-SECRET-VALUE", "UHS-VALUE" })
        {
            Assert.DoesNotContain(secret, string.Join("\n", logs));
        }
    }

    // ======================= 轮询状态 =======================

    [Fact]
    public async Task 轮询_授权挂起后成功()
    {
        var pending = 0;
        var (svc, _, _) = CreateService(req => FullChainResponder(req, () =>
        {
            pending++;
            return pending < 3
                ? RawJson("""{"error":"authorization_pending"}""", HttpStatusCode.BadRequest)
                : RawJson(TokenOkJson);
        }));

        var account = await svc.PollAndCompleteAsync(await svc.RequestDeviceCodeAsync());

        Assert.Equal("Steve", account.PlayerName);
        Assert.Equal(3, pending);
    }

    [Fact]
    public async Task 轮询_slow_down后成功()
    {
        var slowed = false;
        var (svc, _, logs) = CreateService(req => FullChainResponder(req, () =>
            slowed ? RawJson(TokenOkJson)
                   : (slowed = true, RawJson("""{"error":"slow_down"}""", HttpStatusCode.BadRequest)).Item2));

        var account = await svc.PollAndCompleteAsync(await svc.RequestDeviceCodeAsync());

        Assert.Equal("Steve", account.PlayerName);
        Assert.Contains(logs, l => l.Contains("slow_down"));
    }

    [Fact]
    public async Task 轮询_用户拒绝_映射declined错误码()
    {
        var (svc, _, _) = CreateService(req => FullChainResponder(req, () =>
            RawJson("""{"error":"authorization_declined","error_description":"user cancelled"}""", HttpStatusCode.BadRequest)));

        var dc = await svc.RequestDeviceCodeAsync();
        var ex = await Assert.ThrowsAsync<MicrosoftAuthException>(
            () => svc.PollAndCompleteAsync(dc));

        Assert.Equal("declined", ex.Code);
    }

    [Fact]
    public async Task 轮询_设备码过期_映射expired错误码()
    {
        var (svc, _, _) = CreateService(req => FullChainResponder(req, () =>
            RawJson("""{"error":"expired_token"}""", HttpStatusCode.BadRequest)));

        var dc = await svc.RequestDeviceCodeAsync();
        var ex = await Assert.ThrowsAsync<MicrosoftAuthException>(
            () => svc.PollAndCompleteAsync(dc));

        Assert.Equal("expired", ex.Code);
    }

    [Fact]
    public async Task 轮询_用户取消_抛出OperationCanceledException()
    {
        var (svc, _, _) = CreateService(req => FullChainResponder(req, () =>
            RawJson("""{"error":"authorization_pending"}""", HttpStatusCode.BadRequest)));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var deviceCode = await svc.RequestDeviceCodeAsync(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.PollAndCompleteAsync(deviceCode, cts.Token));
    }

    // ======================= 资格与持有权 =======================

    [Fact]
    public async Task XSTS_无Xbox资格_映射no_xbox()
    {
        var (svc, _, _) = CreateService(req =>
            req.RequestUri!.AbsoluteUri.Contains("xsts.auth.xboxlive.com")
                ? RawJson("""{"XErr":2148916233,"Message":"No Xbox account"}""", HttpStatusCode.Unauthorized)
                : FullChainResponder(req));

        var dc = await svc.RequestDeviceCodeAsync();
        var ex = await Assert.ThrowsAsync<MicrosoftAuthException>(
            () => svc.PollAndCompleteAsync(dc));

        Assert.Equal("no_xbox", ex.Code);
    }

    [Fact]
    public async Task XSTS_儿童账户_映射child_account()
    {
        var (svc, _, _) = CreateService(req =>
            req.RequestUri!.AbsoluteUri.Contains("xsts.auth.xboxlive.com")
                ? RawJson("""{"XErr":2148916238,"Message":"Child account"}""", HttpStatusCode.Unauthorized)
                : FullChainResponder(req));

        var dc = await svc.RequestDeviceCodeAsync();
        var ex = await Assert.ThrowsAsync<MicrosoftAuthException>(
            () => svc.PollAndCompleteAsync(dc));

        Assert.Equal("child_account", ex.Code);
    }

    [Fact]
    public async Task profile404_未拥有Minecraft_映射no_game()
    {
        var (svc, _, _) = CreateService(req =>
            req.RequestUri!.AbsoluteUri.Contains("minecraft/profile")
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : FullChainResponder(req));

        var dc = await svc.RequestDeviceCodeAsync();
        var ex = await Assert.ThrowsAsync<MicrosoftAuthException>(
            () => svc.PollAndCompleteAsync(dc));

        Assert.Equal("no_game", ex.Code);
    }

    [Fact]
    public async Task profile500_服务端错误_映射profile_failed()
    {
        var (svc, _, _) = CreateService(req =>
            req.RequestUri!.AbsoluteUri.Contains("minecraft/profile")
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : FullChainResponder(req));

        var dc = await svc.RequestDeviceCodeAsync();
        var ex = await Assert.ThrowsAsync<MicrosoftAuthException>(
            () => svc.PollAndCompleteAsync(dc));

        Assert.Equal("profile_failed", ex.Code);
    }

    // ======================= 网络与续期 =======================

    [Fact]
    public async Task 网络中断_映射network错误码()
    {
        var handler = new ThrowingHandler(new HttpRequestException("connection refused"));
        var svc = new MicrosoftAuthService(handler);

        var ex = await Assert.ThrowsAsync<MicrosoftAuthException>(() => svc.RequestDeviceCodeAsync());

        Assert.Equal("network", ex.Code);
        Assert.Contains("网络", ex.Message);
    }

    [Fact]
    public async Task 刷新令牌过期_映射refresh_failed()
    {
        var (svc, _, _) = CreateService(req => FullChainResponder(req, () =>
            RawJson("""{"error":"invalid_grant","error_description":"token expired"}""", HttpStatusCode.BadRequest)));

        var account = new MicrosoftAccount
        {
            PlayerName = "Steve",
            RefreshToken = "MS-REFRESH-SECRET",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(-1),
        };

        var ex = await Assert.ThrowsAsync<MicrosoftAuthException>(() => svc.RefreshAsync(account));

        Assert.Equal("refresh_failed", ex.Code);
    }

    [Fact]
    public async Task EnsureFresh_未过期_不发起任何请求()
    {
        var (svc, handler, _) = CreateService(req => FullChainResponder(req));

        var account = new MicrosoftAccount
        {
            PlayerName = "Steve",
            AccessToken = "MC-ACCESS-SECRET",
            RefreshToken = "MS-REFRESH-SECRET",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
        };

        var refreshed = await svc.EnsureFreshAsync(account);

        Assert.False(refreshed);
        Assert.Empty(handler.SeenUris);
    }

    [Fact]
    public async Task EnsureFresh_已过期_静默续期成功并更新账户()
    {
        var (svc, _, _) = CreateService(req => FullChainResponder(req));

        var account = new MicrosoftAccount
        {
            PlayerName = "Steve",
            AccessToken = "old-token",
            RefreshToken = "MS-REFRESH-SECRET",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(-1),
        };

        var refreshed = await svc.EnsureFreshAsync(account);

        Assert.True(refreshed);
        Assert.Equal("MC-ACCESS-SECRET", account.AccessToken);
        Assert.False(account.IsExpired);
    }

    [Fact]
    public async Task 设备码申请失败_抛出阶段化异常()
    {
        var handler = new RoutingHandler(_ => RawJson(
            """{"error":"invalid_request","error_description":"bad scope"}""", HttpStatusCode.BadRequest));
        var svc = new MicrosoftAuthService(handler);

        var ex = await Assert.ThrowsAsync<MicrosoftAuthException>(() => svc.RequestDeviceCodeAsync());

        Assert.Equal("device_code_failed", ex.Code);
        Assert.Contains("bad scope", ex.Message);
    }

    [Fact]
    public async Task 登录链路只访问官方HTTPS端点()
    {
        var (svc, handler, _) = CreateService(req => FullChainResponder(req));

        var deviceCode = await svc.RequestDeviceCodeAsync();
        await svc.PollAndCompleteAsync(deviceCode);

        // 清单 §72：不得经过任何下载镜像/代理；全部为官方 HTTPS 端点
        Assert.NotEmpty(handler.SeenUris);
        foreach (var uri in handler.SeenUris)
        {
            Assert.Equal("https", uri.Scheme);
            Assert.True(
                uri.Host is "login.microsoftonline.com" or "user.auth.xboxlive.com"
                    or "xsts.auth.xboxlive.com" or "api.minecraftservices.com",
                $"非官方端点: {uri.Host}");
        }
    }

    private sealed class ThrowingHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw ex;
    }
}
