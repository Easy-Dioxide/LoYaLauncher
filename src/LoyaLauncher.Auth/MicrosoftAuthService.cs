using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LoyaLauncher.Auth;

/// <summary>微软认证失败异常，携带可供 UI 本地化的错误码</summary>
/// <param name="Code">
/// 稳定错误码（UI 据此本地化，不直接展示原始消息）：
/// network / http_error / declined / expired / no_xbox / child_account /
/// no_game / token_failed / refresh_failed / profile_failed / xbox_failed /
/// xsts_failed / mc_login_failed / device_code_failed
/// </param>
public sealed class MicrosoftAuthException : Exception
{
    public string Code { get; }

    public MicrosoftAuthException(string code, string message) : base(message) => Code = code;
}

/// <summary>设备码信息（展示给用户 + 轮询用）</summary>
/// <param name="UserCode">用户需在浏览器输入的验证码</param>
/// <param name="VerificationUri">验证页面地址</param>
/// <param name="VerificationUriComplete">预填验证码的一键直达地址（部分账户不返回，回退为 VerificationUri）</param>
/// <param name="DeviceCode">轮询用的设备码</param>
/// <param name="IntervalSeconds">建议轮询间隔（秒）</param>
/// <param name="ExpiresInSeconds">设备码有效期（秒）</param>
/// <param name="Message">微软返回的引导文案</param>
public sealed record DeviceCodeInfo(
    string UserCode,
    string VerificationUri,
    string VerificationUriComplete,
    string DeviceCode,
    int IntervalSeconds,
    int ExpiresInSeconds,
    string Message);

/// <summary>
/// 微软正版登录服务：OAuth2 设备码流程（consumers 租户，个人微软账户）。
/// 完整令牌链：MS 访问令牌 → Xbox Live 用户认证 → XSTS 授权 → Minecraft Services 登录 → profile。
/// 使用官方 Minecraft 启动器的公开 client_id，无需注册应用或保管密钥。
///
/// 清单 §7 修复点：
/// - 全链路阶段化日志（device-code / token-poll / xbox-auth / xsts-auth / mc-login /
///   profile / refresh），只记录阶段、错误码与关联 ID，绝不记录令牌或设备码；
/// - 网络失败、4xx/5xx、用户拒绝、设备码过期、无 Xbox 资格、儿童账户、未拥有游戏
///   分别映射为不同 <see cref="MicrosoftAuthException.Code"/>；
/// - 只访问官方 HTTPS 端点（常量白名单），不经过任何下载镜像或代理；
/// - HttpClient 可注入（测试用本地 mock），生产仍为单例复用。
/// </summary>
public sealed class MicrosoftAuthService
{
    /// <summary>官方 Minecraft 启动器公开 client_id（设备码流程无需 secret）</summary>
    public const string ClientId = "2d60d4a5-cf6c-4e94-bb78-3d99b53a0e7f";

    private const string DeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    private const string XboxAuthUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string McLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string McProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Action<string>? _log;

    /// <summary>
    /// 生产构造（DI 单例）。测试可注入 mock <see cref="HttpMessageHandler"/>
    /// 与日志收集委托（清单 §11 可控 mock，§7 脱敏日志验证）。
    /// </summary>
    public MicrosoftAuthService(HttpMessageHandler? handler = null, Action<string>? log = null)
    {
        _http = handler is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(30) }
            : new HttpClient(handler, disposeHandler: false);
        _ownsHttp = handler is null;
        _log = log;
    }

    /// <summary>当前登录会话关联 ID（每次 PollAndComplete/Refresh 重新生成，用于串联阶段日志）</summary>
    private string _correlation = "-";

    private void Log(string stage, string outcome, string? code = null) =>
        _log?.Invoke($"[auth:{_correlation}] {stage}: {outcome}{(code is null ? "" : $" code={code}")}");

    // ---------- 设备码流程 ----------

    /// <summary>申请设备码。用户随后在浏览器打开 <paramref name="DeviceCodeInfo.VerificationUri"/> 输入 UserCode。</summary>
    public async Task<DeviceCodeInfo> RequestDeviceCodeAsync(CancellationToken ct = default)
    {
        _correlation = Guid.NewGuid().ToString("N")[..8];
        DeviceCodeResponse data;
        try
        {
            data = await PostFormAsync<DeviceCodeResponse>(DeviceCodeUrl, "device-code", new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["scope"] = "XboxLive.signin offline_access",
            }, ct);
        }
        catch (MicrosoftAuthException ex)
        {
            Log("device-code", "fail", ex.Code);
            throw;
        }

        if (data.device_code is null || data.user_code is null || data.verification_uri is null)
        {
            var code = data.error is null ? "device_code_failed" : "device_code_failed";
            Log("device-code", "fail", code);
            throw new MicrosoftAuthException(code,
                data.error_description ?? data.error ?? "设备码申请失败");
        }

        // 日志只记录存在性与间隔，不记录 user_code / device_code 本体
        Log("device-code", $"ok interval={data.interval} expires_in={data.expires_in}");
        return new DeviceCodeInfo(
            data.user_code,
            data.verification_uri,
            data.verification_uri_complete ?? data.verification_uri,
            data.device_code,
            data.interval is > 0 ? data.interval : 5,
            data.expires_in > 0 ? data.expires_in : 900,
            data.message ?? $"打开 {data.verification_uri} 并输入代码 {data.user_code}");
    }

    /// <summary>
    /// 轮询设备码直至用户完成授权，随后走完整令牌链返回微软账户。
    /// 轮询中取消（ct）会抛出 OperationCanceledException，由调用方静默处理。
    /// </summary>
    public async Task<MicrosoftAccount> PollAndCompleteAsync(DeviceCodeInfo info, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(info.ExpiresInSeconds);
        var interval = Math.Max(1, info.IntervalSeconds);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline)
            {
                Log("token-poll", "fail", "expired");
                throw new MicrosoftAuthException("expired", "设备码已过期，请重新发起登录");
            }

            await Task.Delay(TimeSpan.FromSeconds(interval), ct);

            MsTokenResponse token;
            try
            {
                token = await PostFormAsync<MsTokenResponse>(TokenUrl, "token-poll", new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = ClientId,
                    ["device_code"] = info.DeviceCode,
                }, ct);
            }
            catch (MicrosoftAuthException ex)
            {
                Log("token-poll", "fail", ex.Code);
                throw;
            }

            // 设备码轮询的"等待中"以 HTTP 400 + error 字段表达，属正常状态而非失败
            if (token.error is "authorization_pending")
                continue;
            if (token.error is "slow_down")
            {
                interval += 5;
                Log("token-poll", "slow_down");
                continue;
            }
            if (token.error is not null)
            {
                var code = MapPollError(token.error);
                Log("token-poll", "fail", code);
                throw new MicrosoftAuthException(code, token.error_description ?? token.error);
            }
            if (token.access_token is null || token.refresh_token is null)
            {
                Log("token-poll", "fail", "token_failed");
                throw new MicrosoftAuthException("token_failed", "令牌响应不完整");
            }

            Log("token-poll", "ok");
            try
            {
                return await CompleteLoginAsync(token.access_token, token.refresh_token, token.expires_in, ct);
            }
            finally
            {
                // 无论成败都结束本会话的日志串联
                _correlation = "-";
            }
        }
    }

    // ---------- 续期 ----------

    /// <summary>
    /// 确保账户令牌可用：未过期直接返回 false；已过期则用刷新令牌走完整链路续期并返回 true。
    /// 调用方负责把返回的最新账户信息写回 <see cref="MicrosoftAccount"/> 并持久化。
    /// </summary>
    public async Task<bool> EnsureFreshAsync(MicrosoftAccount account, CancellationToken ct = default)
    {
        if (!account.IsExpired) return false;
        var refreshed = await RefreshAsync(account, ct);
        account.AccessToken = refreshed.AccessToken;
        account.RefreshToken = refreshed.RefreshToken;
        account.ExpiresAtUtc = refreshed.ExpiresAtUtc;
        return true;
    }

    /// <summary>用 MS 刷新令牌重新走完整令牌链，返回最新账户信息（不修改入参）</summary>
    public async Task<MicrosoftAccount> RefreshAsync(MicrosoftAccount account, CancellationToken ct = default)
    {
        _correlation = Guid.NewGuid().ToString("N")[..8];
        try
        {
            if (string.IsNullOrEmpty(account.RefreshToken))
                throw new MicrosoftAuthException("no_refresh_token", "缺少刷新令牌，请重新登录");

            var token = await PostFormAsync<MsTokenResponse>(TokenUrl, "refresh", new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = ClientId,
                ["refresh_token"] = account.RefreshToken,
                ["scope"] = "XboxLive.signin offline_access",
            }, ct);

            if (token.error is not null || token.access_token is null)
            {
                Log("refresh", "fail", "refresh_failed");
                throw new MicrosoftAuthException("refresh_failed",
                    token.error_description ?? "刷新令牌已失效，请重新登录");
            }

            Log("refresh", "ok");
            return await CompleteLoginAsync(token.access_token, token.refresh_token ?? account.RefreshToken, token.expires_in, ct);
        }
        finally
        {
            _correlation = "-";
        }
    }

    // ---------- 令牌链 ----------

    private async Task<MicrosoftAccount> CompleteLoginAsync(string msAccessToken, string msRefreshToken, int expiresIn, CancellationToken ct)
    {
        // 1. Xbox Live 用户认证
        var (xblToken, uhs) = await AuthenticateXboxLiveAsync(msAccessToken, ct);

        // 2. XSTS 授权（RelyingParty 指向 Minecraft Services）
        var xstsToken = await AuthorizeXstsAsync(xblToken, ct);

        // 3. Minecraft Services 登录，换取 MC 访问令牌
        var (mcToken, mcExpiresIn) = await LoginToMinecraftAsync(uhs, xstsToken, ct);

        // 4. 拉取 profile：校验游戏持有权并取角色名/UUID
        var (uuid, name) = await GetProfileAsync(mcToken, ct);

        Log("login-chain", "ok");
        return new MicrosoftAccount
        {
            PlayerName = name,
            PlayerUuid = NormalizeUuid(uuid),
            AccessToken = mcToken,
            RefreshToken = msRefreshToken,
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(mcExpiresIn > 0 ? mcExpiresIn : 86400),
        };
    }

    private async Task<(string token, string uhs)> AuthenticateXboxLiveAsync(string msAccessToken, CancellationToken ct)
    {
        var payload = new
        {
            Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = $"d={msAccessToken}" },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
        };
        XboxAuthResponse? data;
        try
        {
            data = await PostJsonAsync<XboxAuthResponse>(XboxAuthUrl, "xbox-auth", payload, ct);
        }
        catch (MicrosoftAuthException ex)
        {
            Log("xbox-auth", "fail", ex.Code);
            throw;
        }
        var token = data?.Token;
        var uhs = data?.DisplayClaims?.xui?.FirstOrDefault()?.uhs;
        if (token is null || uhs is null)
        {
            Log("xbox-auth", "fail", "xbox_failed");
            throw new MicrosoftAuthException("xbox_failed", "Xbox Live 认证失败");
        }
        Log("xbox-auth", "ok");
        return (token, uhs);
    }

    private async Task<string> AuthorizeXstsAsync(string xblToken, CancellationToken ct)
    {
        var payload = new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xblToken } },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT",
        };
        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsync(XstsUrl, JsonBody(payload), ct);
        }
        catch (HttpRequestException ex)
        {
            Log("xsts-auth", "fail", "network");
            throw new MicrosoftAuthException("network", $"XSTS 授权网络失败: {ex.Message}");
        }

        if (!resp.IsSuccessStatusCode)
        {
            // XSTS 错误体不是标准信封：{"XErr":2148916233,"Message":"...","Redirect":"..."}
            var err = await ReadJsonAsync<XstsErrorResponse>(resp, ct);
            var code = err?.XErr switch
            {
                2148916233 => "no_xbox",
                2148916238 => "child_account",
                _ => "xsts_failed",
            };
            Log("xsts-auth", "fail", code);
            throw code switch
            {
                "no_xbox" => new MicrosoftAuthException(code, "该微软账户没有 Xbox 账号，请先在 Xbox 注册"),
                "child_account" => new MicrosoftAuthException(code, "该账户为儿童账户，需要在家庭账户中批准"),
                _ => new MicrosoftAuthException(code, err?.Message ?? $"XSTS 授权失败（HTTP {(int)resp.StatusCode}）"),
            };
        }
        var data = await ReadJsonAsync<XboxAuthResponse>(resp, ct);
        if (data?.Token is null)
        {
            Log("xsts-auth", "fail", "xsts_failed");
            throw new MicrosoftAuthException("xsts_failed", "XSTS 授权响应缺少令牌");
        }
        Log("xsts-auth", "ok");
        return data.Token;
    }

    private async Task<(string token, int expiresIn)> LoginToMinecraftAsync(string uhs, string xstsToken, CancellationToken ct)
    {
        var payload = new { identityToken = $"XBL3.0 x={uhs};{xstsToken}" };
        McLoginResponse? data;
        try
        {
            data = await PostJsonAsync<McLoginResponse>(McLoginUrl, "mc-login", payload, ct);
        }
        catch (MicrosoftAuthException ex)
        {
            Log("mc-login", "fail", ex.Code);
            throw;
        }
        if (data?.access_token is null)
        {
            Log("mc-login", "fail", "mc_login_failed");
            throw new MicrosoftAuthException("mc_login_failed", "Minecraft Services 登录响应缺少令牌");
        }
        Log("mc-login", "ok");
        return (data.access_token, data.expires_in);
    }

    private async Task<(string uuid, string name)> GetProfileAsync(string mcToken, CancellationToken ct)
    {
        HttpRequestMessage req = new(HttpMethod.Get, McProfileUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcToken);
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            Log("profile", "fail", "network");
            throw new MicrosoftAuthException("network", $"Minecraft 资料网络请求失败: {ex.Message}");
        }

        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            Log("profile", "fail", "no_game");
            throw new MicrosoftAuthException("no_game", "该账户未拥有 Minecraft Java 版");
        }
        if (!resp.IsSuccessStatusCode)
        {
            Log("profile", "fail", "profile_failed");
            throw new MicrosoftAuthException("profile_failed", $"拉取 Minecraft 资料失败（HTTP {(int)resp.StatusCode}）");
        }
        var data = await ReadJsonAsync<McProfileResponse>(resp, ct);
        if (data?.id is null || data.name is null)
        {
            Log("profile", "fail", "profile_failed");
            throw new MicrosoftAuthException("profile_failed", "Minecraft 资料响应不完整");
        }
        Log("profile", "ok");
        return (data.id, data.name);
    }

    // ---------- 基础设施 ----------

    /// <summary>
    /// POST application/x-www-form-urlencoded 并反序列化 JSON。
    /// 传输层故障（DNS/连接/TLS/取消）统一映射为 <c>network</c> 错误码，
    /// 与业务错误（invalid_grant / authorization_pending 等）区分（清单 §7）。
    /// </summary>
    private async Task<T> PostFormAsync<T>(string url, string stage, Dictionary<string, string> form, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsync(url, new FormUrlEncodedContent(form), ct);
        }
        catch (HttpRequestException ex)
        {
            throw new MicrosoftAuthException("network", $"{stage} 网络请求失败: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new MicrosoftAuthException("network", $"{stage} 请求超时");
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        var data = JsonSerializer.Deserialize<T>(json, JsonOpts);
        if (data is null)
        {
            // 设备码/令牌端点失败时返回的是错误信封而非 200 负载：
            // 反序列化出空对象属预期路径，由调用方检查 error 字段
            if (typeof(T) == typeof(DeviceCodeResponse) || typeof(T) == typeof(MsTokenResponse))
                return data; // 字段全 null，调用方按错误处理
            throw new MicrosoftAuthException("http_error",
                $"{stage} 接口返回异常（HTTP {(int)resp.StatusCode}）");
        }
        return data;
    }

    private async Task<T?> PostJsonAsync<T>(string url, string stage, object payload, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsync(url, JsonBody(payload), ct);
        }
        catch (HttpRequestException ex)
        {
            throw new MicrosoftAuthException("network", $"{stage} 网络请求失败: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new MicrosoftAuthException("network", $"{stage} 请求超时");
        }
        return await ReadJsonAsync<T>(resp, ct);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(json, JsonOpts);
    }

    private static StringContent JsonBody(object payload)
        => new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static string MapPollError(string error) => error switch
    {
        "authorization_declined" => "declined",
        "expired_token" => "expired",
        _ => "token_failed",
    };

    /// <summary>profile id 为 32 位无横线十六进制；规范化为 8-4-4-4-12，与离线账户展示一致</summary>
    private static string NormalizeUuid(string raw)
    {
        var hex = raw.Replace("-", "");
        return hex.Length == 32
            ? $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}"
            : raw;
    }

    // ---------- 响应模型 ----------
    // 属性名与各接口实际返回的 JSON 字段精确对应（默认大小写敏感匹配）

    private sealed class DeviceCodeResponse
    {
        public string? device_code { get; set; }
        public string? user_code { get; set; }
        public string? verification_uri { get; set; }
        public string? verification_uri_complete { get; set; }
        public int expires_in { get; set; }
        public int interval { get; set; }
        public string? message { get; set; }
        public string? error { get; set; }
        public string? error_description { get; set; }
    }

    private sealed class MsTokenResponse
    {
        public string? access_token { get; set; }
        public string? refresh_token { get; set; }
        public int expires_in { get; set; }
        public string? token_type { get; set; }
        public string? error { get; set; }
        public string? error_description { get; set; }
    }

    private sealed class XboxAuthResponse
    {
        public string? Token { get; set; }
        public XboxDisplayClaims? DisplayClaims { get; set; }

        public sealed class XboxDisplayClaims
        {
            public List<XboxXuiClaim>? xui { get; set; }
        }

        public sealed class XboxXuiClaim
        {
            public string? uhs { get; set; }
        }
    }

    private sealed class XstsErrorResponse
    {
        public long XErr { get; set; }
        public string? Message { get; set; }
        public string? Redirect { get; set; }
    }

    private sealed class McLoginResponse
    {
        public string? access_token { get; set; }
        public int expires_in { get; set; }
    }

    private sealed class McProfileResponse
    {
        public string? id { get; set; }
        public string? name { get; set; }
    }
}
