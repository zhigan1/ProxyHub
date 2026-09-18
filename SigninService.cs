using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace ProxyHub;

/// <summary>
/// 签到结果：平台无关统一格式。
/// </summary>
public sealed record SigninResult(
    string AdapterId,
    string AccountLabel,
    string Platform,          // "trae" | "workbuddy"
    string Result,            // "CLAIMED" | "ALREADY" | "OK" | "NO_SESSION" | "ERROR" | "LOAD_ERROR" | "INACTIVE"
    string Report,
    int? TotalCredits,
    int? TodayCredit,
    int? StreakDays,
    bool? TodayCheckedIn,
    string? ErrorDetail = null);

/// <summary>
/// 签到服务：支持 Trae（traecn / traework）和 WorkBuddy（codebuddy）两平台。
/// 纯 BCL + HttpClient，无第三方依赖。
/// </summary>
public sealed class SigninService
{
    private static readonly HttpClient _http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    { Timeout = TimeSpan.FromSeconds(30) };

    public DateTimeOffset? LastRunAt { get; private set; }
    public IReadOnlyList<SigninResult>? LastResults { get; private set; }

    private static readonly string[] SigninAdapters = ["codebuddy", "traecn", "traework"];

    public static IReadOnlyList<AdapterAccount> CollectAccounts(ProxyHubRuntime rt) =>
        SigninAdapters.SelectMany(id => rt.Accounts.AccountsOf(id)).ToList();

    /// <summary>为全部账号执行自动签到（顺序执行，防风控）。</summary>
    public async Task<IReadOnlyList<SigninResult>> RunAllAsync(ProxyHubRuntime rt, CancellationToken ct = default)
    {
        var accounts = CollectAccounts(rt);
        var results = new List<SigninResult>();
        foreach (var acc in accounts)
        {
            var r = await SigninAsync(acc, ct);
            results.Add(r);
            if (r.TotalCredits.HasValue)
                rt.Accounts.UpdateCredit(acc.AdapterId, acc.AccountId, r.TotalCredits.Value);
            if (accounts.Count > 1) await Task.Delay(1200, ct);
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
                rt.Accounts.UpdateCredit(acc.AdapterId, acc.AccountId, r.TotalCredits.Value);
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

    // ─── Trae ─────────────────────────────────────────────

    private const string TraeHost = "https://api.trae.cn";
    private const string TraeClientId = "en1oxy7wnw8j9n";
    private const string TraeAppVersion = "1.107.1";

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

    private static HttpRequestMessage TraeRequest(string path, string token, string deviceId)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, TraeHost + path);
        req.Headers.TryAddWithoutValidation("Authorization", $"Cloud-IDE-JWT {token}");
        req.Headers.TryAddWithoutValidation("X-User-Region", "cn");
        req.Headers.TryAddWithoutValidation("x-device-id", deviceId);
        req.Headers.TryAddWithoutValidation("User-Agent", $"Trae/{TraeAppVersion}");
        req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        return req;
    }

    private async Task<SigninResult> TraeGetStatusAsync(AdapterAccount account, CancellationToken ct)
    {
        var creds = LoadTraeCredentials(account);
        if (creds is null)
            return Fail(account, "trae", "LOAD_ERROR", "凭据文件不存在或解密失败", null);

        var (token, deviceId) = creds.Value;
        try
        {
            using var req = TraeRequest("/trae/api/v2/ug/checkin_credits/status", token, deviceId);
            using var resp = await _http.SendAsync(req, ct);
            var body = await ParseJson(resp, ct);
            return BuildTraeResult(account, body, resp.StatusCode, null);
        }
        catch (Exception e)
        {
            return Fail(account, "trae", "ERROR", "网络请求失败", e.Message);
        }
    }

    private async Task<SigninResult> TraeSigninAsync(AdapterAccount account, CancellationToken ct)
    {
        var creds = LoadTraeCredentials(account);
        if (creds is null)
            return Fail(account, "trae", "LOAD_ERROR", "凭据文件不存在或解密失败", null);

        var (token, deviceId) = creds.Value;
        try
        {
            // 1. 查状态
            using var statusReq = TraeRequest("/trae/api/v2/ug/checkin_credits/status", token, deviceId);
            using var statusResp = await _http.SendAsync(statusReq, ct);
            if ((int)statusResp.StatusCode is 401 or 403)
                return Fail(account, "trae", "NO_SESSION", $"登录态失效（HTTP {(int)statusResp.StatusCode}）", null);

            var statusBody = await ParseJson(statusResp, ct);
            var already = Dig<bool?>(statusBody, "checked_in") == true || Dig<bool?>(statusBody, "did_checked_in") == true;
            if (already)
                return BuildTraeResult(account, statusBody, statusResp.StatusCode, "ALREADY");

            // 2. 领取签到
            using var claimReq = TraeRequest("/trae/api/v2/ug/checkin_credits/claim", token, deviceId);
            using var claimResp = await _http.SendAsync(claimReq, ct);
            if ((int)claimResp.StatusCode is 401 or 403)
                return Fail(account, "trae", "NO_SESSION", $"登录态失效（HTTP {(int)claimResp.StatusCode}）", null);

            var claimBody = await ParseJson(claimResp, ct);
            var code = Dig<int?>(claimBody, "code") ?? 0;
            if (code == 9095)
                return BuildTraeResult(account, statusBody, claimResp.StatusCode, "ALREADY");

            var credit = Dig<int?>(claimBody, "credits") ?? Dig<int?>(claimBody, "extra_credits") ?? 50;

            // 3. 刷新最新状态
            using var req2 = TraeRequest("/trae/api/v2/ug/checkin_credits/status", token, deviceId);
            using var resp2 = await _http.SendAsync(req2, ct);
            var fresh = await ParseJson(resp2, ct);

            return BuildTraeResult(account, fresh ?? claimBody, claimResp.StatusCode, "CLAIMED", credit);
        }
        catch (Exception e)
        {
            return Fail(account, "trae", "ERROR", "网络请求失败", e.Message);
        }
    }

    private static SigninResult BuildTraeResult(AdapterAccount acc, JsonNode? body, System.Net.HttpStatusCode code, string? forceResult, int? credit = null)
    {
        if ((int)code is 401 or 403)
            return Fail(acc, "trae", "NO_SESSION", $"登录态失效（HTTP {(int)code}）", null);

        var enable = Dig<bool?>(body, "enable") ?? true;
        if (!enable)
            return new SigninResult(acc.AdapterId, acc.Label, "trae", "INACTIVE", "签到活动未开启", null, null, null, false);

        var credits = Dig<int?>(body, "credits") ?? Dig<int?>(body, "total_credits");
        var checkedIn = Dig<bool?>(body, "checked_in") ?? Dig<bool?>(body, "did_checked_in");
        var todayCredit = credit ?? (checkedIn == true ? (Dig<int?>(body, "extra_credits") ?? 50) : null);

        if (forceResult is null && (int)code is < 200 or >= 300)
            return Fail(acc, "trae", "ERROR", $"查询状态失败（HTTP {(int)code}）", null);

        var result = forceResult ?? (checkedIn == true ? "ALREADY" : "OK");
        var report = result switch
        {
            "CLAIMED" => $"签到成功 +{credit ?? 50}积分 · 累计可用额度 {credits}积分",
            "ALREADY" => $"今日已签到 · 累计可用额度 {credits}积分",
            "OK" => $"可用额度 {credits}积分 · 今日{(checkedIn == true ? "已签" : "未签")}",
            _ => body?["message"]?.GetValue<string>() ?? $"状态码 {(int)code}",
        };
        return new SigninResult(acc.AdapterId, acc.Label, "trae", result, report, credits, todayCredit, null, checkedIn);
    }

    // ─── WorkBuddy ────────────────────────────────────────

    private const string WbDefaultEndpoint = "https://copilot.tencent.com";

    private sealed record WbAccount(string Name, string Uid, string AccessToken, string? EnterpriseId, string? Domain, string Endpoint);

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
                acct?["enterpriseId"]?.ToString(),
                auth?["domain"]?.ToString(),
                ep);
        }
        catch { return null; }
    }

    private static HttpRequestMessage WbRequest(string url, WbAccount wb, HttpMethod? method = null)
    {
        var req = new HttpRequestMessage(method ?? HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", wb.AccessToken);
        req.Headers.TryAddWithoutValidation("X-User-Id", wb.Uid);
        req.Headers.TryAddWithoutValidation("User-Agent", "WorkBuddy");
        if (!string.IsNullOrEmpty(wb.EnterpriseId))
        {
            req.Headers.TryAddWithoutValidation("X-Enterprise-Id", wb.EnterpriseId);
            req.Headers.TryAddWithoutValidation("X-Tenant-Id", wb.EnterpriseId);
        }
        if (!string.IsNullOrEmpty(wb.Domain))
            req.Headers.TryAddWithoutValidation("X-Domain", wb.Domain);
        req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        return req;
    }

    private async Task<SigninResult> WorkBuddyGetStatusAsync(AdapterAccount account, CancellationToken ct)
    {
        var wb = LoadWbCredentials(account);
        if (wb is null) return Fail(account, "workbuddy", "LOAD_ERROR", "凭据文件不存在或格式错误", null);
        try
        {
            using var req = WbRequest($"{wb.Endpoint}/v2/billing/meter/checkin-activity-status", wb);
            using var resp = await _http.SendAsync(req, ct);
            if ((int)resp.StatusCode is 401 or 403)
                return Fail(account, "workbuddy", "NO_SESSION", $"登录态失效（HTTP {(int)resp.StatusCode}）", null);
            if ((int)resp.StatusCode is < 200 or >= 300)
                return Fail(account, "workbuddy", "ERROR", $"查询状态失败（HTTP {(int)resp.StatusCode}）", null);
            var body = await ParseJson(resp, ct);
            if (Dig<bool>(body, "active") == false)
                return new SigninResult(account.AdapterId, account.Label, "workbuddy", "INACTIVE", "签到活动未开启", null, null, null, null);
            return BuildWbResult(account, body, resp.StatusCode, null, null);
        }
        catch (Exception e)
        {
            return Fail(account, "workbuddy", "ERROR", "网络请求失败", e.Message);
        }
    }

    private async Task<SigninResult> WorkBuddySigninAsync(AdapterAccount account, CancellationToken ct)
    {
        var wb = LoadWbCredentials(account);
        if (wb is null) return Fail(account, "workbuddy", "LOAD_ERROR", "凭据文件不存在或格式错误", null);
        try
        {
            // 1. 查状态
            using var statusReq = WbRequest($"{wb.Endpoint}/v2/billing/meter/checkin-activity-status", wb);
            using var statusResp = await _http.SendAsync(statusReq, ct);
            if ((int)statusResp.StatusCode is 401 or 403)
                return Fail(account, "workbuddy", "NO_SESSION", $"登录态失效（HTTP {(int)statusResp.StatusCode}）", null);
            if ((int)statusResp.StatusCode is < 200 or >= 300)
                return Fail(account, "workbuddy", "ERROR", $"查询状态失败（HTTP {(int)statusResp.StatusCode}）", null);

            var statusBody = await ParseJson(statusResp, ct);
            var active = Dig<bool>(statusBody, "active");
            if (active == false)
                return new SigninResult(account.AdapterId, account.Label, "workbuddy", "INACTIVE", "签到活动未开启", null, null, null, null);

            var todayChecked = Dig<bool>(statusBody, "today_checked_in");
            if (todayChecked == true)
                return BuildWbResult(account, statusBody, statusResp.StatusCode, "ALREADY", null);

            // 2. 签到
            using var claimReq = WbRequest($"{wb.Endpoint}/v2/billing/meter/daily-checkin", wb);
            using var claimResp = await _http.SendAsync(claimReq, ct);
            if ((int)claimResp.StatusCode is 401 or 403)
                return Fail(account, "workbuddy", "NO_SESSION", $"登录态失效（HTTP {(int)claimResp.StatusCode}）", null);

            var claimBody = await ParseJson(claimResp, ct);
            var credit = Dig<int?>(claimBody, "credit");

            // 已签到的幂等判断
            if (claimBody is JsonObject co && (Dig<int?>(co, "code") == 10001 || Dig<string>(co, "msg")?.Contains("已签") == true))
                return BuildWbResult(account, statusBody, statusResp.StatusCode, "ALREADY", null);

            if (credit is not null && (int)claimResp.StatusCode is >= 200 and < 300)
            {
                // 刷新状态
                using var req2 = WbRequest($"{wb.Endpoint}/v2/billing/meter/checkin-activity-status", wb);
                using var resp2 = await _http.SendAsync(req2, ct);
                var fresh = await ParseJson(resp2, ct);
                return BuildWbResult(account, fresh ?? statusBody, claimResp.StatusCode, "CLAIMED", credit);
            }

            var errMsg = Dig<string>(claimBody, "msg") ?? $"HTTP {(int)claimResp.StatusCode}";
            return Fail(account, "workbuddy", "ERROR", $"领取失败：{errMsg}", null);
        }
        catch (Exception e)
        {
            return Fail(account, "workbuddy", "ERROR", "网络请求失败", e.Message);
        }
    }

    private static SigninResult BuildWbResult(AdapterAccount acc, JsonNode? body, System.Net.HttpStatusCode code, string? forceResult, int? credit)
    {
        var totalCredits = Dig<int?>(body, "total_credits");
        var streakDays = Dig<int?>(body, "streak_days");
        var todayChecked = Dig<bool?>(body, "today_checked_in");
        var todayCredit = credit ?? Dig<int?>(body, "today_credit") ?? Dig<int?>(body, "daily_credit");

        if (forceResult is null && (int)code is < 200 or >= 300)
            return Fail(acc, "workbuddy", "ERROR", $"查询状态失败（HTTP {(int)code}）", null);

        var result = forceResult ?? "OK"; // 状态查询成功即为 OK，今日是否已签看 TodayCheckedIn 字段
        var report = result switch
        {
            "CLAIMED" => $"签到成功 +{credit}积分 · 连续{streakDays}天 · 累计{totalCredits}积分",
            "ALREADY" => $"今日已签到 · 连续{streakDays}天 · 累计{totalCredits}积分",
            "INACTIVE" => "签到活动未开启",
            "OK" => $"积分 {totalCredits} · 连续{streakDays}天 · 今日{(todayChecked == true ? "已签" : "未签")}",
            _ => $"上游返回异常（HTTP {(int)code}）",
        };
        return new SigninResult(acc.AdapterId, acc.Label, "workbuddy", result, report, totalCredits, todayCredit, streakDays, todayChecked);
    }

    // ─── 通用工具 ──────────────────────────────────────────

    private static async Task<JsonNode?> ParseJson(HttpResponseMessage resp, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct);
        try { return JsonNode.Parse(text); } catch { return null; }
    }

    /// <summary>递归在 data/result/resp/response 嵌套层查找 key。</summary>
    private static T? Dig<T>(JsonNode? node, string key)
    {
        if (node is null) return default;
        if (node is JsonObject obj)
        {
            if (obj.TryGetPropertyValue(key, out var v) && v is JsonValue jv)
            {
                try { return jv.GetValue<T>(); } catch { }
            }
            foreach (var nested in new[] { "data", "result", "resp", "response" })
            {
                if (obj.TryGetPropertyValue(nested, out var child) && child is JsonObject)
                {
                    var found = Dig<T>(child, key);
                    if (found is not null) return found;
                }
            }
        }
        return default;
    }

    private static SigninResult Fail(AdapterAccount acc, string platform, string result, string report, string? detail) =>
        new(acc.AdapterId, acc.Label, platform, result, report, null, null, null, null, detail);
}
