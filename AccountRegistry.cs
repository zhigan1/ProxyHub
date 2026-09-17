using System.Collections.Concurrent;

namespace ProxyHub;

/// <summary>
/// 账号池：合并"自动发现"（适配器扫描本机凭据）与"手工录入"（config.json accounts 段）。
/// 自动发现失败不影响手工账号；热更新只重建手工段，自动发现结果保留。
/// </summary>
public sealed class AccountRegistry
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<AdapterAccount>> _discovered = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<AdapterAccount>> _manual = new();
    private readonly object _gate = new();

    /// <summary>重新执行各适配器的本机账号扫描；单个适配器失败记空列表，不影响其他平台。</summary>
    public async Task RefreshAsync(IReadOnlyList<IAdapter> adapters, CancellationToken ct = default)
    {
        foreach (var adapter in adapters)
        {
            try
            {
                var found = await adapter.DiscoverAccountsAsync(ct).ConfigureAwait(false);
                _discovered[adapter.Id] = found;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                _discovered[adapter.Id] = Array.Empty<AdapterAccount>();
            }
        }
    }

    /// <summary>热更新：整体替换手工账号段。ManualAccount → AdapterAccount 的字段按适配器类型解释。</summary>
    public void SetManual(IReadOnlyDictionary<string, IReadOnlyList<ManualAccount>> specs)
    {
        lock (_gate)
        {
            _manual.Clear();
            foreach (var (adapterId, list) in specs)
            {
                var converted = new List<AdapterAccount>();
                var index = 0;
                foreach (var m in list)
                {
                    var acc = ToAccount(adapterId, m, index++);
                    if (acc is not null) converted.Add(acc);
                }
                _manual[adapterId] = converted;
            }
        }
    }

    /// <summary>手工账号字段按平台解释：CodeBuddy 用 authFile，Trae 系用 storageFile，Qoder 用 pat。</summary>
    private static AdapterAccount? ToAccount(string adapterId, ManualAccount m, int index)
    {
        var label = string.IsNullOrWhiteSpace(m.Label) ? $"manual-{index + 1}" : m.Label.Trim();
        var id = $"manual-{label}";
        return adapterId.ToLowerInvariant() switch
        {
            "codebuddy" => m.AuthFile is null ? null
                : new AdapterAccount(adapterId, id, label, SourceFile: m.AuthFile, Discovered: false),
            "traecn" or "traework" => m.StorageFile is null ? null
                : new AdapterAccount(adapterId, id, label, SourceFile: m.StorageFile, Discovered: false),
            "qoder" => m.Pat is null ? null
                : new AdapterAccount(adapterId, id, label, Pat: m.Pat, Discovered: false),
            _ => null,
        };
    }

    /// <summary>某适配器的全部账号：自动发现在前（默认账号即首项），手工录入殿后。</summary>
    public IReadOnlyList<AdapterAccount> AccountsOf(string adapterId)
    {
        _discovered.TryGetValue(adapterId, out var found);
        _manual.TryGetValue(adapterId, out var manual);
        if ((found is null || found.Count == 0) && (manual is null || manual.Count == 0))
            return Array.Empty<AdapterAccount>();
        var merged = new List<AdapterAccount>(found ?? Array.Empty<AdapterAccount>());
        if (manual is not null) merged.AddRange(manual);
        return merged;
    }

    public int TotalCount => _discovered.Values.Sum(v => v.Count) + _manual.Values.Sum(v => v.Count);

    public int ManualCount => _manual.Values.Sum(v => v.Count);
}
