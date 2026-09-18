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
    string Result,            // "CLAIMED" | "ALREADY" | "NO_SESSION" | "ERROR" | "LOAD_ERROR" | "INACTIVE"
    string Report,
    int? TotalCredits,
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
            if (!key.StartsWith("iCubeAuthInfo://", StringComparison.Ordinal)) continue;
            var rawValue = value?.GetValue<string>();
            if (rawValue is null) continue;

            string? decrypted;
            try { decrypted = TcCrypto.DecryptTc(rawValue); }
            catch { continue; }

            if (key == "iCubeAuthInfo://icube.cloudide")
            {
                var obj = JsonNode.Parse(decrypted) as JsonObject;
                token = obj?["token"]?.GetValue<string>();
            }
            else if (key.StartsWith("iCubeAuthInfo://icube-dc:", StringComparison.Ordinal))
            {
                var candidate = key["iCubeAuthInfo://icube-dc:".Length..].Trim();
                if (System.Text.RegularExpressions.Regex.IsMatch(candidate, @"^\d{8,20}$"))
                    deviceId = candidate;
            }
        }

        if (token is null) return null;

        // 若未找到设备 ID，生成随机 16 位数字 ID
        deviceId ??= (Random.Shared.NextInt64(1_000_000_000_000_000L, 9_999_999_999_999_999L)).ToString();
        return (token, deviceId);
    }

    private static HttpRequestMessage TraeRequest(string path, string token, string deviceId, HttpMethod? method = null)
    {
        var req = new HttpRequestMessage(method ?? HttpMethod.Post, TraeHost + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("x-client-id", TraeClientId);
        req.Headers.TryAddWithoutValidation("x-app-version", TraeAppVersion);
        req.Headers.TryAddWithoutValidation("x-device-id", deviceId);
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
            using var req = TraeRequest("/ide/user/signin/status", token, deviceId, HttpMethod.Get);
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
            using var statusReq = TraeRequest("/ide/user/signin/status", token, deviceId, HttpMethod.Get);
            using var statusResp = await _http.SendAsync(statusReq, ct);
            var statusBody = await ParseJson(statusResp, ct);

            var todayChecked = statusBody?["today_checked_in"]?.GetValue<bool>();
            if (todayChecked == true)
                return BuildTraeResult(account, statusBody, statusResp.StatusCode, "ALREADY");

            // 2. 签到
            using var claimReq = TraeRequest("/ide/user/signin", token, deviceId);
            using var claimResp = await _http.SendAsync(claimReq, ct);
            if ((int)claimResp.StatusCode is 401 or 403)
                return Fail(account, "trae", "NO_SESSION", $"登录态失效（HTTP {(int)claimResp.StatusCode}）", null);

            var claimBody = await ParseJson(claimResp, ct);

            // 3. 刷新状态（获取最新积分）
            using var req2 = TraeRequest("/ide/user/signin/status", token, deviceId, HttpMethod.Get);
            using var resp2 = await _http.SendAsync(req2, ct);
            var fresh = await ParseJson(resp2, ct);

            return BuildTraeResult(account, fresh ?? claimBody, claimResp.StatusCode, "CLAIMED",
                Dig<int?>(claimBody, "credit") ?? Dig<int?>(claimBody, "credits"));
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

        var totalCredits = Dig<int?>(body, "total_credits") ?? Dig<int?>(body, "totalCredits");
        var streakDays = Dig<int?>(body, "streak_days") ?? Dig<int?>(body, "streakDays");
        var todayChecked = Dig<bool?>(body, "today_checked_in") ?? Dig<bool?>(body, "todayCheckedIn");

        var result = forceResult ?? (todayChecked == true ? "ALREADY" : "UNKNOWN");
        var report = result switch
        {
            "CLAIMED" => $"签到成功 +{credit}积分 · 连续{streakDays}天 · 累计{totalCredits}积分",
            "ALREADY" => $"今日已签到 · 连续{streakDays}天 · 累计{totalCredits}积分",
            _ => body?.ToJsonString()[..Math.Min(120, body.ToJsonString().Length)] ?? "未知",
        };
        return new SigninResult(acc.AdapterId, acc.Label, "trae", result, report, totalCredits, streakDays, todayChecked);
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
            var uid = acct?["uid"]?.GetValue<string>();
            var token = auth?["accessToken"]?.GetValue<string>();
            if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(token)) return null;
            var ep = auth?["endpoint"]?.GetValue<string>() ?? WbDefaultEndpoint;
            ep = ep.TrimEnd('/');
            return new WbAccount(
                acct?["nickname"]?.GetValue<string>() ?? uid,
                uid, token,
                acct?["enterpriseId"]?.GetValue<string>(),
                auth?["domain"]?.GetValue<string>(),
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
            var body = await ParseJson(resp, ct);
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

            var statusBody = await ParseJson(statusResp, ct);
            var active = Dig<bool>(statusBody, "active");
            if (active == false)
                return new SigninResult(account.AdapterId, account.Label, "workbuddy", "INACTIVE", "签到活动未开启", null, null, null);

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
        var result = forceResult ?? (todayChecked == true ? "ALREADY" : "UNKNOWN");
        var report = result switch
        {
            "CLAIMED" => $"签到成功 +{credit}积分 · 连续{streakDays}天 · 累计{totalCredits}积分",
            "ALREADY" => $"今日已签到 · 连续{streakDays}天 · 累计{totalCredits}积分",
            "INACTIVE" => "签到活动未开启",
            _ => body?.ToJsonString()[..Math.Min(120, body.ToJsonString().Length)] ?? "未知",
        };
        return new SigninResult(acc.AdapterId, acc.Label, "workbuddy", result, report, totalCredits, streakDays, todayChecked);
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
        new(acc.AdapterId, acc.Label, platform, result, report, null, null, null, detail);
}
