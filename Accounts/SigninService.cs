using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProxyHub;

/// <summary>
/// 签到结果：平台无关统一格式。
/// CreditsUsed/CreditsTotal/PackCount 为 WorkBuddy get-user-resource 官方契约聚合（纯增量，查询失败或非 WB 平台为 null）；
/// TotalCredits 语义不变 = 当前剩余可用积分（= creditsRemain）。
/// </summary>
public sealed record SigninResult(
    string AdapterId,
    string AccountId,
    string AccountLabel,
    string Platform,          // "trae" | "workbuddy" | "qoder"
    string Result,            // "CLAIMED" | "ALREADY" | "OK" | "NO_SESSION" | "SESSION_DEAD" | "ERROR" | "LOAD_ERROR" | "INACTIVE" | "UNSUPPORTED" | "SKIPPED"
    string Report,
    int? TotalCredits,
    int? TodayCredit,
    int? StreakDays,
    bool? TodayCheckedIn,
    long? CreditsUsed = null,
    long? CreditsTotal = null,
    int? PackCount = null,
    string? ErrorDetail = null);

/// <summary>
/// 签到服务：支持 Trae（traecn / traework）和 WorkBuddy（codebuddy）两平台。
/// 纯 BCL + HttpClient，无第三方依赖。
///
/// 风控/韧性策略（对齐参考实现 workbuddy-checkin 与 trae-signin-gui 的实测结论）：
/// - Trae UG 请求体必须为官方契约 {"req_source":2}（与 SOLO 谱系 ClientID 配套），空 body 的 claim 会被 9074「当前参与用户太多」拒绝；
/// - 上游拥塞以 HTTP 200 + 文案送达（code 不可作判据），积分并未到账：单次退避重试，仍拥塞报 ERROR，绝不兜底成 CLAIMED；
/// - WorkBuddy 401/403 时用 refreshToken 换新（tmp+rename 原子写回凭据文件，保留唯一 .bak），失败维持 NO_SESSION；
/// - 会话死亡（精确命中 12153 或 "Offline user session not found"）→ SESSION_DEAD，批量流程内自动禁用账号（可一键恢复）；
/// - 串行执行 + 账号间隔防风控；连续多个账号拥塞时提前结束本轮，避免整批干等。
/// </summary>
public sealed class SigninService
{
    public static readonly string[] SigninAdapters = ["codebuddy", "traecn", "traework"];

    private readonly HttpClient _http;
    private readonly TimeSpan _congestionBackoff;
    private readonly TimeSpan _interAccountDelay;

    /// <summary>连续拥塞达到该账号数后提前结束本轮（其余账号记 SKIPPED）。</summary>
    private const int CongestionAbortThreshold = 3;

    /// <summary>拥塞判定文案（HTTP 200 也会拒绝，code 不可作判据，只认文案）。</summary>
    private static readonly string[] CongestionHints =
        ["太多", "拥挤", "繁忙", "忙碌", "稍后", "重试", "too many", "busy", "later", "retry"];

    /// <summary>会话死亡精确标记：只认这两个，网络异常/超时/5xx 一律不得判 SESSION_DEAD。</summary>
    private static readonly string[] SessionDeadMarkers = ["12153", "Offline user session not found"];

    public DateTimeOffset? LastRunAt { get; private set; }
    public IReadOnlyList<SigninResult>? LastResults { get; private set; }

    /// <summary>handler / 退避 / 间隔可注入（测试传假 handler 与零间隔）；生产用默认。</summary>
    public SigninService(HttpMessageHandler? handler = null, TimeSpan? congestionBackoff = null, TimeSpan? interAccountDelay = null)
    {
        _congestionBackoff = congestionBackoff ?? TimeSpan.FromSeconds(20);
        _interAccountDelay = interAccountDelay ?? TimeSpan.FromMilliseconds(1200);
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(15),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            { Timeout = TimeSpan.FromSeconds(30) }
            : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public static IReadOnlyList<AdapterAccount> CollectAccounts(ProxyHubRuntime rt) =>
        SigninAdapters.SelectMany(id => rt.Accounts.AccountsOf(id)).ToList();

    /// <summary>为全部账号执行自动签到（顺序执行，防风控；连续拥塞提前结束本轮）。</summary>
    public async Task<IReadOnlyList<SigninResult>> RunAllAsync(ProxyHubRuntime rt, CancellationToken ct = default)
    {
        var accounts = CollectAccounts(rt);
        var results = new List<SigninResult>();
        var consecutiveCongestion = 0;
        foreach (var acc in accounts)
        {
            if (consecutiveCongestion >= CongestionAbortThreshold)
            {
                results.Add(new SigninResult(acc.AdapterId, acc.AccountId, acc.Label, PlatformOf(acc.AdapterId),
                    "SKIPPED", "上游连续拥塞，本轮提前结束，本账号未尝试", null, null, null, null));
                continue;
            }

            var r = await SigninAsync(acc, ct);
            results.Add(r);
            if (r.TotalCredits.HasValue)
                rt.Accounts.UpdateCredit(acc.AdapterId, acc.AccountId, r.TotalCredits.Value, rt.ConfigStore);
            if (IsSessionDead(r)) DisableAccount(rt, acc);
            consecutiveCongestion = IsCongestion(r) ? consecutiveCongestion + 1 : 0;
            if (accounts.Count > 1) await Task.Delay(_interAccountDelay, ct);
        }
        LastRunAt = DateTimeOffset.UtcNow;
        LastResults = results;
        return results;
    }

    /// <summary>查询全部账号签到状态与积分额度。</summary>
    public async Task<IReadOnlyList<SigninResult>> GetStatusAllAsync(ProxyHubRuntime rt, CancellationToken ct = default)
    {
        var accounts = CollectAccounts(rt);
        var results = new List<SigninResult>();
        foreach (var acc in accounts)
        {
            var r = await GetStatusAsync(acc, ct);
            results.Add(r);
            if (r.TotalCredits.HasValue)
                rt.Accounts.UpdateCredit(acc.AdapterId, acc.AccountId, r.TotalCredits.Value, rt.ConfigStore);
            if (IsSessionDead(r)) DisableAccount(rt, acc);
        }
        return results;
    }

    // ─── 公共入口 ───────────────────────────────────────────

    /// <summary>签到（先查状态，已签跳过，未签才领取）。</summary>
    public Task<SigninResult> SigninAsync(AdapterAccount account, CancellationToken ct = default)
        => IsTrae(account.AdapterId)
            ? TraeSigninAsync(account, ct)
            : WorkBuddySigninAsync(account, ct);

    /// <summary>仅查询签到状态 + 积分，不执行领取。</summary>
    public Task<SigninResult> GetStatusAsync(AdapterAccount account, CancellationToken ct = default)
        => IsTrae(account.AdapterId)
            ? TraeGetStatusAsync(account, ct)
            : WorkBuddyGetStatusAsync(account, ct);

    private static bool IsTrae(string adapterId) =>
        adapterId.Equals("traecn", StringComparison.OrdinalIgnoreCase) ||
        adapterId.Equals("traework", StringComparison.OrdinalIgnoreCase);

    private static string PlatformOf(string adapterId) => IsTrae(adapterId) ? "trae" : "workbuddy";

    private static bool IsSessionDead(SigninResult r) => r.Result == "SESSION_DEAD";

    /// <summary>拥塞终态判定：以 ErrorDetail 中的「上游拥塞」标记为准（SetCongestion 时写入）。</summary>
    private static bool IsCongestion(SigninResult r) => r.ErrorDetail is not null && r.ErrorDetail.Contains("上游拥塞", StringComparison.Ordinal);

    /// <summary>
    /// 会话死亡自动禁用（持久化到 config.json accountSettings，管理页账号页可见、可一键恢复）。
    /// 账号禁用后即退出生效账号池，因此本方法至多对同一账号生效一次。
    /// </summary>
    private static void DisableAccount(ProxyHubRuntime rt, AdapterAccount acc)
    {
        try
        {
            rt.Accounts.ToggleAccount(acc.AdapterId, acc.AccountId, enabled: false, rt.ConfigStore);
            Console.WriteLine($"[Signin] 账号 {acc.AdapterId}/{acc.AccountId}({acc.Label}) 会话死亡(12153)，已自动禁用 —— 登录态失效，需重新登录客户端。可在管理页账号页一键恢复。");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Signin] 自动禁用账号 {acc.AdapterId}/{acc.AccountId} 失败：{e.Message}");
        }
    }

    // ─── Trae ─────────────────────────────────────────────

    private const string TraeHost = "https://api.trae.cn";
    private const string TraeAppVersion = "1.107.1";

    /// <summary>官方客户端契约体：req_source=2 是 SOLO 谱系（ClientID en1oxy7wnw8j9n）产品标识，status 与 claim 共用。</summary>
    private static readonly string TraeCheckinBody = new JsonObject { ["req_source"] = 2 }.ToJsonString();

    private static (string token, string deviceId)? LoadTraeCredentials(AdapterAccount account)
    {
        var file = account.SourceFile;
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;

        JsonObject? storage;
        try { storage = JsonNode.Parse(File.ReadAllText(file)) as JsonObject; }
        catch { return null; }
        if (storage is null) return null;

        string? token = null;
        string? deviceId = null;

        foreach (var (key, value) in storage)
        {
            // deviceId 来自键名（iCubeAuthInfo://icube-dc:{数字}），无需解密值
            if (key.StartsWith("iCubeAuthInfo://icube-dc:", StringComparison.Ordinal))
            {
                var candidate = key["iCubeAuthInfo://icube-dc:".Length..].Trim();
                if (System.Text.RegularExpressions.Regex.IsMatch(candidate, @"^\d{8,20}$"))
                    deviceId = candidate;
                continue;
            }
            if (key != "iCubeAuthInfo://icube.cloudide") continue;

            var rawValue = value?.GetValue<string>();
            if (rawValue is null) continue;

            string? decrypted;
            try { decrypted = rawValue.TrimStart().StartsWith('{') ? rawValue : TcCrypto.DecryptTc(rawValue); }
            catch { continue; }

            var obj = JsonNode.Parse(decrypted) as JsonObject;
            token = obj?["token"]?.GetValue<string>();
        }

        if (token is null) return null;

        // 若未找到设备 ID，生成随机 16 位数字 ID
        deviceId ??= (Random.Shared.NextInt64(1_000_000_000_000_000L, 9_999_999_999_999_999L)).ToString();
        return (token, deviceId);
    }

    private static HttpRequestMessage TraeRequest(string path, string token, string deviceId, string? body = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, TraeHost + path);
        req.Headers.TryAddWithoutValidation("Authorization", $"Cloud-IDE-JWT {token}");
        req.Headers.TryAddWithoutValidation("X-User-Region", "cn");
        req.Headers.TryAddWithoutValidation("x-device-id", deviceId);
        req.Headers.TryAddWithoutValidation("User-Agent", $"Trae/{TraeAppVersion}");
        req.Content = new StringContent(body ?? TraeCheckinBody, System.Text.Encoding.UTF8, "application/json");
        return req;
    }

    private async Task<SigninResult> TraeGetStatusAsync(AdapterAccount account, CancellationToken ct)
    {
        var creds = LoadTraeCredentials(account);
        if (creds is null)
            return Fail(account, "LOAD_ERROR", "凭据文件不存在或解密失败", null);

        var (token, deviceId) = creds.Value;
        try
        {
            var entCreditsTask = FetchTraeCreditsAsync(token, deviceId, ct);
            using var req = TraeRequest("/trae/api/v2/ug/checkin_credits/status", token, deviceId);
            using var resp = await _http.SendAsync(req, ct);
            var body = await ParseJson(resp, ct);
            var entCredits = await entCreditsTask;
            return BuildTraeResult(account, body, (int)resp.StatusCode, null, fallbackCredits: entCredits);
        }
        catch (Exception e)
        {
            return Fail(account, "ERROR", "网络请求失败", e.Message);
        }
    }

    private async Task<SigninResult> TraeSigninAsync(AdapterAccount account, CancellationToken ct)
    {
        var creds = LoadTraeCredentials(account);
        if (creds is null)
            return Fail(account, "LOAD_ERROR", "凭据文件不存在或解密失败", null);

        var (token, deviceId) = creds.Value;
        try
        {
            // 1. 查状态
            using var statusReq = TraeRequest("/trae/api/v2/ug/checkin_credits/status", token, deviceId);
            using var statusResp = await _http.SendAsync(statusReq, ct);
            if ((int)statusResp.StatusCode is 401 or 403)
                return Fail(account, "NO_SESSION", $"登录态失效（HTTP {(int)statusResp.StatusCode}）", null);

            var statusBody = await ParseJson(statusResp, ct);
            var already = Dig<bool?>(statusBody, "checked_in", "checkedIn") == true
                          || Dig<bool?>(statusBody, "did_checked_in", "didCheckedIn") == true;
            if (already)
                return BuildTraeResult(account, statusBody, (int)statusResp.StatusCode, "ALREADY");

            // 2. 领取签到。拥塞响应以 HTTP 200 + 文案送达（与 code 无关），积分并未到账：
            //    单次退避重试，仍拥塞报 ERROR（含「上游拥塞」），绝不兜底成 CLAIMED。
            var (claimBody, claimCode) = await TraeClaimAsync(token, deviceId, ct);

            if (claimCode is 401 or 403)
                return Fail(account, "NO_SESSION", $"登录态失效（HTTP {claimCode}）", null);
            if (claimCode >= 400)
                return Fail(account, "ERROR", $"领取失败（HTTP {claimCode}）", null);

            if (claimBody is not null && IsCongestionText(claimBody))
            {
                await Task.Delay(_congestionBackoff, ct);
                (claimBody, claimCode) = await TraeClaimAsync(token, deviceId, ct);
                if (claimCode is 401 or 403)
                    return Fail(account, "NO_SESSION", $"登录态失效（HTTP {claimCode}）", null);
            }

            if (claimBody is not null && IsCongestionText(claimBody))
                return Fail(account, "ERROR",
                    $"上游拥塞，签到未生效（{CongestionMessage(claimBody)}），请稍后重试", "上游拥塞");

            var code = Dig<int?>(claimBody, "code") ?? 0;
            if (code == 9095)
                return BuildTraeResult(account, statusBody, claimCode, "ALREADY");

            var credit = Dig<int?>(claimBody, "credits") ?? Dig<int?>(claimBody, "extra_credits", "extraCredits") ?? 50;

            // 3. 刷新最新状态
            using var req2 = TraeRequest("/trae/api/v2/ug/checkin_credits/status", token, deviceId);
            using var resp2 = await _http.SendAsync(req2, ct);
            var fresh = await ParseJson(resp2, ct);

            return BuildTraeResult(account, fresh ?? claimBody, claimCode, "CLAIMED", claimedCredit: credit);
        }
        catch (Exception e)
        {
            return Fail(account, "ERROR", "网络请求失败", e.Message);
        }
    }

    private async Task<(JsonObject? Body, int Code)> TraeClaimAsync(string token, string deviceId, CancellationToken ct)
    {
        using var req = TraeRequest("/trae/api/v2/ug/checkin_credits/claim", token, deviceId);
        using var resp = await _http.SendAsync(req, ct);
        var body = await ParseJson(resp, ct) as JsonObject;
        return (body, (int)resp.StatusCode);
    }

    /// <summary>拥塞判定：只看 message/msg 文案命中关键词，与 code 取值无关（code=0 也可能拥塞）。</summary>
    private static bool IsCongestionText(JsonNode? body)
    {
        var msg = Dig<string>(body, "message", "msg");
        if (string.IsNullOrEmpty(msg)) return false;
        return CongestionHints.Any(h => msg.Contains(h, StringComparison.OrdinalIgnoreCase));
    }

    private static string CongestionMessage(JsonNode? body) => Dig<string>(body, "message", "msg") ?? "上游繁忙";

    /// <summary>
    /// Trae 积分余额专用端点 ide_user_ent_usage：sum(userEntitlementPackList[].…quota.creditsLimit)。
    /// 独立于签到活动；失败静默返回 null。
    /// </summary>
    private async Task<int?> FetchTraeCreditsAsync(string token, string deviceId, CancellationToken ct)
    {
        try
        {
            using var req = TraeRequest("/trae/api/v2/pay/ide_user_ent_usage", token, deviceId, "{}");
            using var resp = await _http.SendAsync(req, ct);
            if ((int)resp.StatusCode is < 200 or >= 300) return null;
            var body = await ParseJson(resp, ct);
            var packs = Dig<JsonArray>(body, "userEntitlementPackList") ?? FindDeep<JsonArray>(body, "userEntitlementPackList");
            if (packs is null || packs.Count == 0) return null;
            long total = 0;
            foreach (var pack in packs)
                if (FindDeep<long?>(pack, "creditsLimit") is { } limit) total += limit;
            return total > 0 ? (int?)total : null;
        }
        catch { return null; }
    }

    private static SigninResult BuildTraeResult(AdapterAccount acc, JsonNode? body, int code, string? forceResult, int? claimedCredit = null, int? fallbackCredits = null)
    {
        if (code is 401 or 403)
            return Fail(acc, "NO_SESSION", $"登录态失效（HTTP {code}）", null);

        var enable = Dig<bool?>(body, "enable") ?? true;
        if (!enable)
            return new SigninResult(acc.AdapterId, acc.AccountId, acc.Label, "trae", "INACTIVE", "签到活动未开启", null, null, null, false);

        var credits = Dig<int?>(body, "credits", "total_credits", "totalCredits") ?? fallbackCredits;
        var checkedIn = Dig<bool?>(body, "checked_in", "checkedIn") ?? Dig<bool?>(body, "did_checked_in", "didCheckedIn");
        var todayCredit = claimedCredit ?? (checkedIn == true ? (Dig<int?>(body, "extra_credits", "extraCredits") ?? 50) : null);

        if (forceResult is null && code is < 200 or >= 300)
            return Fail(acc, "ERROR", $"查询状态失败（HTTP {code}）", null);

        var result = forceResult ?? (checkedIn == true ? "ALREADY" : "OK");
        var report = result switch
        {
            "CLAIMED" => $"签到成功 +{claimedCredit ?? 50}积分 · 累计可用额度 {credits}积分",
            "ALREADY" => $"今日已签到 · 累计可用额度 {credits}积分",
            "OK" => $"可用额度 {credits}积分 · 今日{(checkedIn == true ? "已签" : "未签")}",
            _ => body?["message"]?.GetValue<string>() ?? $"状态码 {code}",
        };
        return new SigninResult(acc.AdapterId, acc.AccountId, acc.Label, "trae", result, report, credits, todayCredit, null, checkedIn);
    }

    // ─── WorkBuddy ────────────────────────────────────────

    private const string WbDefaultEndpoint = "https://copilot.tencent.com";

    private sealed record WbAccount(
        string Name, string Uid, string AccessToken, string? RefreshToken, long? ExpiresAt,
        string? EnterpriseId, string? Domain, string Endpoint, string AuthFile);

    /// <summary>WorkBuddy 积分资源汇总（get-user-resource 聚合；total_size 已按 TotalDosage 校准，total_used 已按 size-remain 补全）。</summary>
    private sealed record WbCreditsSummary(long TotalRemain, long TotalUsed, long TotalSize, int PackCount);

    private static WbAccount? LoadWbCredentials(AdapterAccount account)
    {
        var file = account.SourceFile;
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
        try
        {
            var obj = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
            if (obj is null) return null;
            var auth = obj["auth"] as JsonObject;
            var acct = obj["account"] as JsonObject;
            var uid = acct?["uid"]?.ToString(); // uid 可能是数字，ToString 宽容处理
            var token = auth?["accessToken"]?.GetValue<string>();
            if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(token)) return null;
            var ep = auth?["endpoint"]?.GetValue<string>() ?? WbDefaultEndpoint;
            ep = ep.TrimEnd('/');
            return new WbAccount(
                acct?["nickname"]?.ToString() ?? uid,
                uid, token,
                auth?["refreshToken"]?.GetValue<string>(),
                ParseExpiresAt(auth?["expiresAt"]),
                acct?["enterpriseId"]?.ToString(),
                auth?["domain"]?.ToString(),
                ep,
                file);
        }
        catch { return null; }
    }

    private static long? ParseExpiresAt(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        try { if (v.TryGetValue<double>(out var d) && d > 0) return (long)d; } catch { }
        return null;
    }

    private static HttpRequestMessage WbRequest(string url, WbAccount wb, HttpMethod? method = null)
    {
        var req = new HttpRequestMessage(method ?? HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", wb.AccessToken);
        req.Headers.TryAddWithoutValidation("X-User-Id", wb.Uid);
        req.Headers.TryAddWithoutValidation("X-Domain", !string.IsNullOrEmpty(wb.Domain) ? wb.Domain : "www.codebuddy.cn");
        req.Headers.TryAddWithoutValidation("X-Product", "SaaS");
        req.Headers.TryAddWithoutValidation("X-IDE-Type", "CLI");
        req.Headers.TryAddWithoutValidation("x-codebuddy-request", "1");
        req.Headers.TryAddWithoutValidation("User-Agent", "CLI/2.136.0 CodeBuddy/2.136.0");
        if (!string.IsNullOrEmpty(wb.EnterpriseId))
        {
            req.Headers.TryAddWithoutValidation("X-Enterprise-Id", wb.EnterpriseId);
            req.Headers.TryAddWithoutValidation("X-Tenant-Id", wb.EnterpriseId);
        }
        req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        return req;
    }

    /// <summary>WB 响应三元组：状态码 / 原始文本（会话死亡判定用）/ 解析后的 JSON。</summary>
    private sealed record WbReply(int StatusCode, string Text, JsonNode? Body);

    private async Task<WbReply> WbPostAsync(WbAccount wb, Func<WbAccount, HttpRequestMessage> build, CancellationToken ct)
    {
        using var req = build(wb);
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        return new WbReply((int)resp.StatusCode, text, ParseJsonText(text));
    }

    /// <summary>
    /// 仅当 HTTP 401/403 且响应体精确命中 12153 或 "Offline user session not found" 才判会话死亡；
    /// 网络异常、超时、5xx 一律不得判 SESSION_DEAD（防误伤）。
    /// </summary>
    private static bool IsSessionDeadBody(int statusCode, string rawText) =>
        statusCode is 401 or 403 &&
        (rawText.Contains("12153", StringComparison.Ordinal) ||
         rawText.Contains("Offline user session not found", StringComparison.OrdinalIgnoreCase));

    private static string WbErrorText(string text) => text.Length <= 300 ? text : text[..300];

    /// <summary>
    /// 用 refreshToken 换新 accessToken（对齐 workbuddy-checkin keepalive 逻辑）。
    /// 仅当确实拿到新 token 才写回凭据文件；任何失败返回 null（维持 NO_SESSION，不写坏凭据）。
    /// </summary>
    private async Task<WbAccount?> RefreshWbTokenAsync(WbAccount wb, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(wb.RefreshToken)) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{wb.Endpoint}/v2/plugin/auth/token/refresh");
            req.Headers.TryAddWithoutValidation("X-Refresh-Token", wb.RefreshToken);
            req.Headers.TryAddWithoutValidation("X-Auth-Refresh-Source", "workbuddy");
            req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return null;
            if ((int)resp.StatusCode is < 200 or >= 300) return null;

            var body = await ParseJson(resp, ct);
            var newToken = Dig<string>(body, "accessToken", "access_token", "token");
            if (string.IsNullOrEmpty(newToken)) return null; // 未确实拿到新 token：不写回

            var newRefresh = Dig<string>(body, "refreshToken", "refresh_token") ?? wb.RefreshToken;
            long? expiresAt = wb.ExpiresAt;
            var expiresIn = Dig<long?>(body, "expiresIn", "expires_in");
            if (expiresIn is > 0) expiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn.Value;

            WriteWbAuthFile(wb.AuthFile, wb.AccessToken, newToken, newRefresh, expiresAt);
            return wb with { AccessToken = newToken, RefreshToken = newRefresh, ExpiresAt = expiresAt };
        }
        catch { return null; }
    }

    /// <summary>凭据写回：tmp 原子写 → 备份唯一一份 .bak → rename 覆盖。任一步失败都不碰原文件。</summary>
    private static void WriteWbAuthFile(string file, string oldAccessToken, string accessToken, string refreshToken, long? expiresAt)
    {
        var tmp = file + ".signin.tmp";
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
            if (root?["auth"] is not JsonObject auth) return; // 结构对不上：放弃写回，保持原样

            auth["accessToken"] = accessToken;
            auth["refreshToken"] = refreshToken;
            if (expiresAt.HasValue) auth["expiresAt"] = expiresAt.Value;

            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            // 替换前备份（仅保留一份 .bak，旧 .bak 覆盖）
            File.Copy(file, file + ".bak", overwrite: true);

            File.Move(tmp, file, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            // 写回失败不影响原凭据文件：宁可维持旧 token / NO_SESSION，也不能写坏凭据
        }
    }

    private async Task<SigninResult> WorkBuddyGetStatusAsync(AdapterAccount account, CancellationToken ct)
    {
        var wb = LoadWbCredentials(account);
        if (wb is null) return Fail(account, "LOAD_ERROR", "凭据文件不存在或格式错误", null);
        try
        {
            var availTask = FetchWbCreditsAsync(wb, ct);
            var reply = await WbPostAsync(wb, w => WbRequest($"{w.Endpoint}/v2/billing/meter/checkin-activity-status", w), ct);

            if (IsSessionDeadBody(reply.StatusCode, reply.Text))
                return Fail(account, "SESSION_DEAD", "登录态失效，需重新登录客户端（已自动禁用，可在账号页一键恢复）", WbErrorText(reply.Text));

            var avail = await availTask;
            if (reply.StatusCode is 401 or 403)
            {
                var refreshed = await RefreshWbTokenAsync(wb, ct);
                if (refreshed is null)
                    return Fail(account, "NO_SESSION", $"登录态失效（HTTP {reply.StatusCode}）", null);
                wb = refreshed;
                avail = await FetchWbCreditsAsync(wb, ct); // 新 token 重新取额度
                reply = await WbPostAsync(wb, w => WbRequest($"{w.Endpoint}/v2/billing/meter/checkin-activity-status", w), ct);
            }

            if (reply.StatusCode is < 200 or >= 300)
                return Fail(account, "ERROR", $"查询状态失败（HTTP {reply.StatusCode}）", null);

            if (Dig<bool>(reply.Body, "active", "Active") == false && avail is null)
                return new SigninResult(account.AdapterId, account.AccountId, account.Label, "workbuddy", "INACTIVE", "签到活动未开启", null, null, null, null);

            return BuildWbResult(account, reply.Body, reply.StatusCode, null, null, avail);
        }
        catch (Exception e)
        {
            return Fail(account, "ERROR", "网络请求失败", e.Message);
        }
    }

    private async Task<SigninResult> WorkBuddySigninAsync(AdapterAccount account, CancellationToken ct)
    {
        var wb = LoadWbCredentials(account);
        if (wb is null) return Fail(account, "LOAD_ERROR", "凭据文件不存在或格式错误", null);
        try
        {
            // 1. 查状态（内含会话死亡判定与 401/403 刷新重试）
            var (decided, statusReplyOpt, refreshedWb) = await WbStatusWithRefreshAsync(account, wb, ct);
            if (decided is not null) return decided; // SESSION_DEAD / NO_SESSION / ERROR 已定论
            var statusReply = statusReplyOpt!;
            wb = refreshedWb;

            var avail = await FetchWbCreditsAsync(wb, ct);

            var active = Dig<bool>(statusReply.Body, "active", "Active");
            if (active == false && avail is null)
                return new SigninResult(account.AdapterId, account.AccountId, account.Label, "workbuddy", "INACTIVE", "签到活动未开启", null, null, null, null);

            var todayChecked = Dig<bool>(statusReply.Body, "today_checked_in", "todayCheckedIn");
            if (todayChecked == true)
                return BuildWbResult(account, statusReply.Body, statusReply.StatusCode, "ALREADY", null, avail);

            // 2. 签到
            var claim = await WbPostAsync(wb, w => WbRequest($"{w.Endpoint}/v2/billing/meter/daily-checkin", w), ct);

            if (IsSessionDeadBody(claim.StatusCode, claim.Text))
                return Fail(account, "SESSION_DEAD", "登录态失效，需重新登录客户端（已自动禁用，可在账号页一键恢复）", WbErrorText(claim.Text));

            if (claim.StatusCode is 401 or 403)
            {
                var refreshed = await RefreshWbTokenAsync(wb, ct);
                if (refreshed is null)
                    return Fail(account, "NO_SESSION", $"登录态失效（HTTP {claim.StatusCode}）", null);
                wb = refreshed;
                claim = await WbPostAsync(wb, w => WbRequest($"{w.Endpoint}/v2/billing/meter/daily-checkin", w), ct);
            }

            var credit = Dig<int?>(claim.Body, "credit", "Credit");

            // 已签到的幂等判断
            if (claim.Body is JsonObject co && (Dig<int?>(co, "code") == 10001 || Dig<string>(co, "msg", "message")?.Contains("已签") == true))
                return BuildWbResult(account, statusReply.Body, statusReply.StatusCode, "ALREADY", null, avail);

            if (credit is not null && claim.StatusCode is >= 200 and < 300)
            {
                // 刷新状态与额度
                var freshAvail = await FetchWbCreditsAsync(wb, ct);
                var fresh = await WbPostAsync(wb, w => WbRequest($"{w.Endpoint}/v2/billing/meter/checkin-activity-status", w), ct);
                return BuildWbResult(account, fresh.Body ?? statusReply.Body, claim.StatusCode, "CLAIMED", credit, freshAvail ?? avail);
            }

            var errMsg = Dig<string>(claim.Body, "msg", "message") ?? $"HTTP {claim.StatusCode}";
            return Fail(account, "ERROR", $"领取失败：{errMsg}", null);
        }
        catch (Exception e)
        {
            return Fail(account, "ERROR", "网络请求失败", e.Message);
        }
    }

    /// <summary>查活动状态：先判会话死亡 → 401/403 刷新重试 → 状态码判定。Result 非空表示已定论。</summary>
    private async Task<(SigninResult? Result, WbReply? Reply, WbAccount Wb)> WbStatusWithRefreshAsync(AdapterAccount account, WbAccount wb, CancellationToken ct)
    {
        var reply = await WbPostAsync(wb, w => WbRequest($"{w.Endpoint}/v2/billing/meter/checkin-activity-status", w), ct);

        if (IsSessionDeadBody(reply.StatusCode, reply.Text))
            return (Fail(account, "SESSION_DEAD", "登录态失效，需重新登录客户端（已自动禁用，可在账号页一键恢复）", WbErrorText(reply.Text)), null, wb);

        if (reply.StatusCode is 401 or 403)
        {
            var refreshed = await RefreshWbTokenAsync(wb, ct);
            if (refreshed is null)
                return (Fail(account, "NO_SESSION", $"登录态失效（HTTP {reply.StatusCode}）", null), null, wb);
            wb = refreshed;
            reply = await WbPostAsync(wb, w => WbRequest($"{w.Endpoint}/v2/billing/meter/checkin-activity-status", w), ct);
        }

        if (reply.StatusCode is < 200 or >= 300)
            return (Fail(account, "ERROR", $"查询状态失败（HTTP {reply.StatusCode}）", null), null, wb);

        return (null, reply, wb);
    }

    /// <summary>
    /// WorkBuddy 积分资源查询（逐行对齐参考实现 fetch_user_resource / billing.go fetchUserResource 的官方契约）：
    /// - 请求体日期为字符串 "yyyy-MM-dd HH:mm:ss"，上界固定 "2126-12-31 23:59:59"；
    /// - 响应为 {code,msg,data} 信封，资源结构在 data.Response.Data.Accounts[]；
    /// - 单包取值三层回退（WbPackageRemainUsed），总量经 TotalDosage 校准、used 由 size-remain 补全；
    /// - 空套餐列表视为无数据返回 null（调用方回退签到 status 的 total_credits，对齐参考实现的 falsy 语义）。
    /// </summary>
    private async Task<WbCreditsSummary?> FetchWbCreditsAsync(WbAccount wb, CancellationToken ct)
    {
        try
        {
            var body = new JsonObject
            {
                ["PageNumber"] = 1,
                ["PageSize"] = 100,
                ["ProductCode"] = "p_tcaca",
                ["Status"] = new JsonArray { 0, 3 },
                ["PackageEndTimeRangeBegin"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["PackageEndTimeRangeEnd"] = "2126-12-31 23:59:59",
            };
            var reply = await WbPostAsync(wb, w =>
            {
                var req = WbRequest($"{w.Endpoint}/v2/billing/meter/get-user-resource", w);
                req.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
                return req;
            }, ct);
            if (reply.StatusCode is < 200 or >= 300 || reply.Body is not JsonObject root)
                return null;

            // 官方 {code,msg,data} 信封：code!=0 视为业务错误（对齐参考 http_post_json）
            if (Dig<int?>(root, "code") is { } bizCode && bizCode != 0)
                return null;

            // 官方嵌套结构：data.Response.Data.Accounts[]（信封 data → Response → Data → Accounts；
            // 逐层宽容：无信封或无 Response 包装时回退上一层继续找）
            var dataNode = root["data"] as JsonObject ?? root;
            var respNode = dataNode["Response"] as JsonObject ?? dataNode;
            var respData = respNode["Data"] as JsonObject;
            if (respData?["Accounts"] is not JsonArray accounts || accounts.Count == 0)
                return null;

            long totalRemain = 0, totalUsed = 0, totalSize = 0;
            foreach (var pkg in accounts)
            {
                var (remain, used, size) = WbPackageRemainUsed(pkg);
                totalRemain += remain;
                totalUsed += used;
                totalSize += size;
            }

            // used 补全：总量已知时以 size-remain 推导（取较大者）
            if (totalSize > 0)
            {
                var derived = Math.Max(totalSize - totalRemain, 0);
                if (derived > totalUsed) totalUsed = derived;
            }

            // TotalDosage 校准：上游总 dosage 大于套餐包合计 size 时，以 dosage 为准并回推 used
            var dosage = Dig<long?>(respData, "TotalDosage", "totalDosage");
            if (dosage is > 0 && dosage > totalSize)
            {
                totalSize = dosage.Value;
                var derived = totalSize - totalRemain;
                if (derived > totalUsed) totalUsed = Math.Max(derived, 0);
            }

            return new WbCreditsSummary(totalRemain, totalUsed, totalSize, accounts.Count);
        }
        catch { return null; }
    }

    /// <summary>
    /// 单个套餐包的 remain/used/size 三层回退（逐行对齐参考 _package_remain_used / billing.go packageRemainUsed）：
    /// ① CycleCapacitySize&gt;0 → 周期指标：remain 封顶于 size，used=size-remain 与显式 used 取大者并回正 remain；
    /// ② CycleCapacityRemain/Used 任一&gt;0 → size=remain+used，CapacitySize 可抬升 size 并回正 used；
    /// ③ 生命周期指标 CapacityRemain/Used/Size：size 缺省补齐，used==0 时由 size-remain 推导。
    /// </summary>
    private static (long Remain, long Used, long Size) WbPackageRemainUsed(JsonNode? pkg)
    {
        long Num(string pascal, string camel) => Dig<long?>(pkg, pascal, camel) ?? 0;

        // ① 周期指标（有周期容量）
        var cycleSize = Num("CycleCapacitySize", "cycleCapacitySize");
        if (cycleSize > 0)
        {
            var remain = Math.Max(Num("CycleCapacityRemain", "cycleCapacityRemain"), 0);
            if (remain > cycleSize) remain = cycleSize;
            var used = cycleSize - remain;
            var explicitUsed = Num("CycleCapacityUsed", "cycleCapacityUsed");
            if (explicitUsed > used)
            {
                used = explicitUsed;
                if (cycleSize >= used) remain = cycleSize - used;
            }
            return (remain, used, cycleSize);
        }

        // ② 周期 remain/used 但无周期容量
        var cycleRemain = Num("CycleCapacityRemain", "cycleCapacityRemain");
        var cycleUsed = Num("CycleCapacityUsed", "cycleCapacityUsed");
        if (cycleRemain > 0 || cycleUsed > 0)
        {
            var remain = Math.Max(cycleRemain, 0);
            var used = Math.Max(cycleUsed, 0);
            var size = remain + used;
            var capSize = Num("CapacitySize", "capacitySize");
            if (capSize > size)
            {
                size = capSize;
                if (size >= remain) used = size - remain;
            }
            return (remain, used, size);
        }

        // ③ 生命周期指标
        var lifeRemain = Math.Max(Num("CapacityRemain", "capacityRemain"), 0);
        var lifeUsed = Math.Max(Num("CapacityUsed", "capacityUsed"), 0);
        var lifeSize = Num("CapacitySize", "capacitySize");
        if (lifeSize <= 0) lifeSize = lifeRemain + lifeUsed;
        if (lifeUsed == 0 && lifeSize > lifeRemain) lifeUsed = lifeSize - lifeRemain;
        return (lifeRemain, lifeUsed, lifeSize);
    }

    private static SigninResult BuildWbResult(AdapterAccount acc, JsonNode? body, int code, string? forceResult, int? credit, WbCreditsSummary? credits = null)
    {
        // 官方资源查询命中时以真实聚合为准（含真实零余额）；未命中回退签到 status 的 total_credits
        var totalCredits = credits is not null ? (int?)credits.TotalRemain : Dig<int?>(body, "total_credits", "totalCredits");
        var streakDays = Dig<int?>(body, "streak_days", "streakDays");
        var todayChecked = Dig<bool?>(body, "today_checked_in", "todayCheckedIn");
        var todayCredit = credit ?? Dig<int?>(body, "today_credit", "todayCredit") ?? Dig<int?>(body, "daily_credit", "dailyCredit");

        if (forceResult is null && code is < 200 or >= 300)
            return Fail(acc, "ERROR", $"查询状态失败（HTTP {code}）", null);

        var result = forceResult ?? "OK"; // 状态查询成功即为 OK，今日是否已签看 TodayCheckedIn 字段
        var report = result switch
        {
            "CLAIMED" => $"签到成功 +{credit}积分 · 连续{streakDays}天 · 可用额度 {totalCredits}积分",
            "ALREADY" => $"今日已签到 · 连续{streakDays}天 · 可用额度 {totalCredits}积分",
            "INACTIVE" => "签到活动未开启",
            "OK" => $"可用额度 {totalCredits}积分 · 连续{streakDays}天 · 今日{(todayChecked == true ? "已签" : "未签")}",
            _ => $"上游返回异常（HTTP {code}）",
        };
        return new SigninResult(acc.AdapterId, acc.AccountId, acc.Label, "workbuddy", result, report,
            totalCredits, todayCredit, streakDays, todayChecked,
            credits?.TotalUsed, credits?.TotalSize, credits?.PackCount);
    }

    // ─── 通用工具 ──────────────────────────────────────────

    private static async Task<JsonNode?> ParseJson(HttpResponseMessage resp, CancellationToken ct)
    {
        return ParseJsonText(await resp.Content.ReadAsStringAsync(ct));
    }

    private static JsonNode? ParseJsonText(string text)
    {
        try { return JsonNode.Parse(text); } catch { return null; }
    }

    /// <summary>递归在 data/result/resp/response 嵌套层查找 key（支持 snake_case / camelCase 多候选，先到先得）。</summary>
    private static T? Dig<T>(JsonNode? node, params string[] keys)
    {
        foreach (var key in keys)
        {
            T? found;
            if (TryDigOne(node, key, out found))
                return found;
        }
        return default;
    }

    private static bool TryDigOne<T>(JsonNode? node, string key, out T? value)
    {
        value = default;
        if (node is not JsonObject obj) return false;

        if (obj.TryGetPropertyValue(key, out var v) && v is not null)
        {
            if (typeof(T) == typeof(JsonArray))
            {
                if (v is JsonArray ja) { value = (T)(object)ja; return true; }
            }
            else if (v is JsonValue jv)
            {
                var coerced = Coerce<T>(jv);
                if (coerced is not null) { value = coerced; return true; }
            }
        }

        foreach (var nested in new[] { "data", "result", "resp", "response", "Data", "Result" })
        {
            if (obj.TryGetPropertyValue(nested, out var child) && child is JsonObject && TryDigOne<T>(child, key, out value))
                return true;
        }
        return false;
    }

    /// <summary>全树深度查找首个命中 key 的值（对齐参考实现 find_key 语义；用于任意深度嵌套字段）。</summary>
    private static T? FindDeep<T>(JsonNode? node, string key)
    {
        if (node is JsonObject obj)
        {
            foreach (var (k, v) in obj)
            {
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                {
                    if (typeof(T) == typeof(JsonArray) && v is JsonArray ja) return (T)(object)ja;
                    if (v is JsonValue jv)
                    {
                        var coerced = Coerce<T>(jv);
                        if (coerced is not null) return coerced;
                    }
                }
                var nested = FindDeep<T>(v, key);
                if (nested is not null) return nested;
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var found = FindDeep<T>(item, key);
                if (found is not null) return found;
            }
        }
        return default;
    }

    /// <summary>宽容取值：直接类型匹配失败后按字符串/数字/布尔逐类尝试。</summary>
    private static T? Coerce<T>(JsonValue jv)
    {
        try
        {
            var direct = jv.GetValue<T>();
            if (direct is not null) return direct;
        }
        catch { }

        var text = jv.ToString();
        if (typeof(T) == typeof(int?) || typeof(T) == typeof(int))
        {
            if (int.TryParse(text, out var i)) return (T)(object)i;
            if (double.TryParse(text, out var d)) return (T)(object)(int)d;
        }
        if (typeof(T) == typeof(long?) || typeof(T) == typeof(long))
        {
            if (long.TryParse(text, out var l)) return (T)(object)l;
            if (double.TryParse(text, out var d)) return (T)(object)(long)d;
        }
        if (typeof(T) == typeof(bool?) || typeof(T) == typeof(bool))
        {
            if (bool.TryParse(text, out var b)) return (T)(object)b;
            if (text == "1") return (T)(object)true;
            if (text == "0") return (T)(object)false;
        }
        if (typeof(T) == typeof(string)) return (T)(object)text;
        return default;
    }

    private static SigninResult Fail(AdapterAccount acc, string result, string report, string? detail) =>
        new(acc.AdapterId, acc.AccountId, acc.Label, PlatformOf(acc.AdapterId), result, report,
            null, null, null, null, ErrorDetail: detail);
}
