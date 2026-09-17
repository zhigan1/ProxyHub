using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ProxyHub.Adapters;

/// <summary>
/// CodeBuddy / WorkBuddy 适配器。
/// 凭据：扫描 %LOCALAPPDATA%\CodeBuddyExtension\Data\Public\auth\*.info 全目录（每个文件=一个账号，桌面端自动刷新 token）。
/// 协议：copilot.tencent.com/v2 标准 OpenAI SSE，重头模拟 CodeBuddy CLI 客户端。
/// 模型：支持多层动态发现（本地服务端下发缓存 -> 产品清单 -> CLI 命令行探测），动态优先，静态兜底。
/// </summary>
public sealed class CodeBuddyAdapter : IAdapter
{
    private const string UpstreamBase = "https://copilot.tencent.com/v2";

    // 固定请求头模拟：模拟 CLI 客户端，证明是合法的 CodeBuddy CLI 请求
    private static readonly Dictionary<string, string> FixedHeaders = new()
    {
        ["X-Domain"] = "www.codebuddy.cn",
        ["X-Product"] = "SaaS",
        ["X-IDE-Type"] = "CLI",
        ["X-IDE-Name"] = "CLI",
        ["X-IDE-Version"] = "2.136.0",
        ["User-Agent"] = "CLI/2.136.0 CodeBuddy/2.136.0",
        ["X-Requested-With"] = "XMLHttpRequest",
        ["x-codebuddy-request"] = "1",
        ["X-Agent-Intent"] = "craft",
        ["X-Agent-Purpose"] = "conversation",
        ["X-Private-Data"] = "false",
    };

    private static readonly string[] BaselineModels =
    {
        "deepseek-flash", "deepseek-v4-pro", "deepseek-v4-flash", "deepseek-v4.1-flash",
        "minimax-m3", "minimax-m2.7", "glm-5.3", "glm-5.3-flash", "glm-5.2", "glm-5.1",
        "glm-5v-turbo", "kimi-k3-1", "kimi-k2.8-preview", "kimi-k2.7", "kimi-k2.6",
        "hy4-preview", "hy3", "hy3-x", "hunyuan-chat",
    };

    private static readonly Regex CliHelpSupportedRegex =
        new(@"Currently supported:\s*\(([^)]+)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly TimeSpan _timeout;
    private readonly string _cliCommand;
    private readonly string? _authDirOverride;

    public CodeBuddyAdapter(TimeSpan? timeout = null, string? cliCommand = null, string? authDir = null)
    {
        _timeout = timeout ?? TimeSpan.FromMilliseconds(120_000);
        _cliCommand = cliCommand ?? Environment.GetEnvironmentVariable("CODEBUDDY_CLI") ?? "codebuddy";
        _authDirOverride = authDir ?? Environment.GetEnvironmentVariable("CODEBUDDY_AUTH_DIR");
    }

    public string Id => "codebuddy";

    public static string DefaultAuthDir()
    {
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
        return Path.Combine(local, "CodeBuddyExtension", "Data", "Public", "auth");
    }

    private string AuthDir => _authDirOverride ?? DefaultAuthDir();

    /// <summary>凭据文件优先级：workbuddy-desktop 最先，其余按文件名稳定排序。</summary>
    private static int AuthFileRank(string fileName) =>
        fileName.StartsWith("workbuddy-desktop", StringComparison.OrdinalIgnoreCase) ? 0
        : fileName.StartsWith("Tencent-Cloud.coding-copilot", StringComparison.OrdinalIgnoreCase) ? 1
        : 2;

    private IEnumerable<FileInfo> AuthFilesOrdered()
    {
        var files = new List<FileInfo>();
        try
        {
            var dir = new DirectoryInfo(AuthDir);
            if (dir.Exists)
                files.AddRange(dir.GetFiles("*.info"));
        }
        catch
        {
            // 目录不可访问视为无凭据
        }
        return files
            .OrderBy(f => AuthFileRank(f.Name))
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>枚举全部可用账号：auth 目录下每个含有效 token 的 *.info 文件。</summary>
    public Task<IReadOnlyList<AdapterAccount>> DiscoverAccountsAsync(CancellationToken ct = default)
    {
        var accounts = new List<AdapterAccount>();
        foreach (var f in AuthFilesOrdered())
        {
            if (TryReadAuth(f.FullName, out _, out _))
                accounts.Add(new AdapterAccount(Id, Path.GetFileNameWithoutExtension(f.Name), f.Name, SourceFile: f.FullName));
        }
        return Task.FromResult<IReadOnlyList<AdapterAccount>>(accounts);
    }

    /// <summary>解析凭据文件为 { token, uid }；损坏/缺失返回 false。</summary>
    private static bool TryReadAuth(string file, out string? token, out string? uid)
    {
        token = uid = null;
        try
        {
            var data = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
            token = data?["auth"]?["accessToken"]?.GetValue<string>();
            uid = data?["account"]?["uid"]?.ToString();
            return !string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(uid);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读指定账号凭据；account 为 null 时按优先级取第一个有效文件（Fail Fast）。</summary>
    public Task<AuthInfo> GetAuthAsync(AdapterAccount? account = null, CancellationToken ct = default)
    {
        // 手工账号可能指向不存在的文件：先试账号文件，无效则回落到发现顺序
        if (account?.SourceFile is { } file && TryReadAuth(file, out var t1, out var u1))
            return Task.FromResult(new AuthInfo(Token: t1, Uid: u1));

        foreach (var f in AuthFilesOrdered())
        {
            if (TryReadAuth(f.FullName, out var t2, out var u2))
                return Task.FromResult(new AuthInfo(Token: t2, Uid: u2));
        }
        throw new InvalidOperationException("未找到 CodeBuddy/WorkBuddy 登录凭据，请先登录 WorkBuddy 客户端。");
    }

    // WorkBuddy 客户端会自动刷新 token 到文件，每次读取即可拿到最新 token
    public Task<AuthInfo> RefreshAuthAsync(AdapterAccount? account = null, CancellationToken ct = default) =>
        GetAuthAsync(account, ct);

    public IReadOnlyList<ModelRegistration> RegisterModels() =>
        BaselineModels.Select(m => new ModelRegistration($"codebuddy-{m}", m)).ToArray();

    /// <summary>
    /// 动态获取模型列表：按策略多层探测
    /// 1. 扫描 ~/.codebuddy/local_storage/entry_*.info 动态服务端下发缓存
    /// 2. 扫描 WorkBuddy / CodeBuddy 安装目录下的 product.json / product.internal.json
    /// 3. 执行 codebuddy --help 提取当前支持模型
    /// 4. 若全部失败则返回 null，由 Registry 回退至 RegisterModels 的静态兜底
    /// </summary>
    public async Task<IReadOnlyList<ModelRegistration>?> FetchModelsAsync(CancellationToken ct = default)
    {
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. 本地动态服务端下发缓存
        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var storageDir = Path.Combine(userProfile, ".codebuddy", "local_storage");
            if (Directory.Exists(storageDir))
            {
                var files = new DirectoryInfo(storageDir)
                    .GetFiles("entry_*.info")
                    .OrderByDescending(f => f.LastWriteTimeUtc);

                foreach (var file in files)
                {
                    try
                    {
                        var content = await File.ReadAllTextAsync(file.FullName, ct).ConfigureAwait(false);
                        var models = ParseLocalStorageInfo(content);
                        foreach (var m in models) discovered.Add(m);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* 容错：单个文件解析失败忽略 */ }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 目录不存在或访问失败忽略 */ }

        // 2. 本地安装产品清单文件
        try
        {
            var candidatePaths = GetProductJsonCandidatePaths();
            foreach (var path in candidatePaths)
            {
                if (File.Exists(path))
                {
                    try
                    {
                        var content = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                        var models = ParseProductJson(content);
                        foreach (var m in models) discovered.Add(m);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* 忽略单个清单解析失败 */ }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 忽略异常 */ }

        // 3. 执行 CLI 探测 (codebuddy --help)
        if (discovered.Count == 0)
        {
            try
            {
                var psi = new ProcessStartInfo(_cliCommand)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("--help");

                using var child = Process.Start(psi);
                if (child != null)
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(5));

                    var stdoutTask = child.StandardOutput.ReadToEndAsync(cts.Token);
                    var stderrTask = child.StandardError.ReadToEndAsync(cts.Token);
                    await child.WaitForExitAsync(cts.Token).ConfigureAwait(false);

                    var output = (await stdoutTask.ConfigureAwait(false)) + "\n" + (await stderrTask.ConfigureAwait(false));
                    var models = ParseCliHelpOutput(output);
                    foreach (var m in models) discovered.Add(m);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* CLI 不可用或超时忽略 */ }
        }

        if (discovered.Count == 0)
            return null;

        return discovered
            .Select(m => new ModelRegistration($"{Id}-{m}", m))
            .ToList();
    }

    private static IEnumerable<string> GetProductJsonCandidatePaths()
    {
        var envPath = Environment.GetEnvironmentVariable("CODEBUDDY_PRODUCT_PATH");
        if (!string.IsNullOrEmpty(envPath)) yield return envPath;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(userProfile, ".codebuddy", "product.json");

        var p86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(p86))
        {
            yield return Path.Combine(p86, "Code", "WorkBuddy", "resources", "app.asar.unpacked", "cli", "product.json");
            yield return Path.Combine(p86, "Code", "WorkBuddy", "resources", "app.asar.unpacked", "cli", "product.internal.json");
            yield return Path.Combine(p86, "CodeBuddy CN", "resources", "app", "product-ide-cn.json");
        }

        var p64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(p64) && !string.Equals(p64, p86, StringComparison.OrdinalIgnoreCase))
        {
            yield return Path.Combine(p64, "Code", "WorkBuddy", "resources", "app.asar.unpacked", "cli", "product.json");
            yield return Path.Combine(p64, "Code", "WorkBuddy", "resources", "app.asar.unpacked", "cli", "product.internal.json");
        }

        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localApp))
        {
            yield return Path.Combine(localApp, "Programs", "CodeBuddy", "resources", "app.asar.unpacked", "cli", "product.json");
            yield return Path.Combine(localApp, "Programs", "CodeBuddy", "resources", "app.asar.unpacked", "cli", "product.internal.json");
        }
    }

    public static List<string> ParseLocalStorageInfo(string jsonContent)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(jsonContent)) return result;

        try
        {
            var node = JsonNode.Parse(jsonContent);
            if (node is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is JsonObject obj)
                        ExtractModelsFromData(obj, result);
                }
            }
            else if (node is JsonObject obj)
            {
                ExtractModelsFromData(obj, result);
            }
        }
        catch { /* JSON 格式不合法或包含非 JSON 数据 */ }

        return result;
    }

    private static void ExtractModelsFromData(JsonObject root, List<string> result)
    {
        var data = root["data"] as JsonObject ?? root;

        // 1. data.agents[].models
        if (data["agents"] is JsonArray agents)
        {
            foreach (var agent in agents)
            {
                if (agent?["models"] is JsonArray agentModels)
                {
                    foreach (var m in agentModels)
                    {
                        var name = m?.GetValue<string>();
                        if (IsChatModel(name) && !result.Contains(name!, StringComparer.OrdinalIgnoreCase))
                            result.Add(name!);
                    }
                }
            }
        }

        // 2. data.models[].id
        if (data["models"] is JsonArray models)
        {
            foreach (var m in models)
            {
                var id = m?["id"]?.GetValue<string>() ?? (m as JsonValue)?.GetValue<string>();
                if (IsChatModel(id) && !result.Contains(id!, StringComparer.OrdinalIgnoreCase))
                    result.Add(id!);
            }
        }
    }

    public static List<string> ParseProductJson(string jsonContent)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(jsonContent)) return result;

        try
        {
            var node = JsonNode.Parse(jsonContent);
            if (node?["models"] is JsonArray models)
            {
                foreach (var m in models)
                {
                    var id = m?["id"]?.GetValue<string>();
                    if (IsChatModel(id) && !result.Contains(id!, StringComparer.OrdinalIgnoreCase))
                        result.Add(id!);
                }
            }
        }
        catch { /* 格式不合法 */ }

        return result;
    }

    public static List<string> ParseCliHelpOutput(string helpOutput)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(helpOutput)) return result;

        var match = CliHelpSupportedRegex.Match(helpOutput);
        if (match.Success)
        {
            var list = match.Groups[1].Value;
            foreach (var item in list.Split(','))
            {
                var trimmed = item.Trim();
                if (IsChatModel(trimmed) && !result.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                    result.Add(trimmed);
            }
        }

        return result;
    }

    public static bool IsChatModel(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return false;
        var trimmed = modelId.Trim();
        if (trimmed.Equals("default", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return false;
        if (trimmed.StartsWith("completion-", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("codewise-", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith("-completion", StringComparison.OrdinalIgnoreCase))
            return false;
        if (trimmed.StartsWith("custom-local:", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    public async Task ChatAsync(JsonObject request, AdapterAccount? account, Func<JsonObject, ValueTask> emit, CancellationToken ct = default)
    {
        var auth = await GetAuthAsync(account, ct);
        var headers = new Dictionary<string, string>(FixedHeaders)
        {
            ["Authorization"] = $"Bearer {auth.Token}",
            ["X-User-Id"] = auth.Uid!,
            ["Accept"] = "text/event-stream",
        };

        using var res = await UpstreamHttp.PostJsonAsync($"{UpstreamBase}/chat/completions", headers, request, _timeout, ct);
        if ((int)res.StatusCode >= 400)
        {
            var errBody = await res.Content.ReadAsStringAsync(ct);
            throw new UpstreamException((int)res.StatusCode,
                $"CodeBuddy upstream {(int)res.StatusCode}: {errBody[..Math.Min(200, errBody.Length)]}");
        }

        // 行为标准 OpenAI SSE，将 data: 的原始行转发为 chunk（含 usage 字段）
        await foreach (var (_, data) in res.ReadSseAsync(ct))
        {
            if (data == "[DONE]") continue;
            if (JsonNode.Parse(data) is JsonObject chunk)
                await emit(chunk);
        }
    }
}
