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
    private readonly ConcurrentDictionary<string, bool> _disabled = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _deleted = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _customOrder = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _credits = new(StringComparer.OrdinalIgnoreCase);
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

    private static AdapterAccount? ToAccount(string adapterId, ManualAccount m, int index)
    {
        var label = string.IsNullOrWhiteSpace(m.Label) ? $"manual-{index + 1}" : m.Label.Trim();
        var id = $"manual-{label}";
        return adapterId.ToLowerInvariant() switch
        {
            "codebuddy" => m.AuthFile is null ? null
                : new AdapterAccount(adapterId, id, label, SourceFile: m.AuthFile, Discovered: false, UserId: label, Order: 100 + index),
            "traecn" or "traework" => m.StorageFile is null ? null
                : new AdapterAccount(adapterId, id, label, SourceFile: m.StorageFile, Discovered: false, UserId: label, Order: 100 + index),
            "qoder" => m.Pat is null ? null
                : new AdapterAccount(adapterId, id, label, Pat: m.Pat, Discovered: false, UserId: label, Order: 100 + index),
            _ => null,
        };
    }

    /// <summary>某适配器的全部账号列表（含禁用与启用，供管理后台展示与调整）。</summary>
    public IReadOnlyList<AdapterAccount> AllAccountsOf(string adapterId)
    {
        _discovered.TryGetValue(adapterId, out var found);
        _manual.TryGetValue(adapterId, out var manual);
        var merged = new List<AdapterAccount>();
        if (found is not null)
            merged.AddRange(found.Where(a => !_deleted.ContainsKey(AccountKey(adapterId, a.AccountId))));
        if (manual is not null)
            merged.AddRange(manual.Where(a => !_deleted.ContainsKey(AccountKey(adapterId, a.AccountId))));

        return merged.Select((a, idx) =>
        {
            var key = AccountKey(adapterId, a.AccountId);
            var isEnabled = !_disabled.ContainsKey(key);
            var initialOrder = a.Order > 0 ? a.Order : (idx + 1);
            var order = _customOrder.TryGetValue(key, out var o) ? o : initialOrder;
            var credits = _credits.TryGetValue(key, out var c) ? (int?)c : a.Credits;
            return a with { Enabled = isEnabled, Order = order, Credits = credits };
        }).OrderBy(a => a.Order).ToList();
    }

    /// <summary>某适配器当前生效且启用的账号列表（严格按账号顺序排序，供候选链构建使用）。</summary>
    public IReadOnlyList<AdapterAccount> AccountsOf(string adapterId)
    {
        return AllAccountsOf(adapterId)
            .Where(a => a.Enabled)
            .ToList();
    }

    public void ToggleAccount(string adapterId, string accountId, bool enabled)
    {
        var key = AccountKey(adapterId, accountId);
        if (enabled) _disabled.TryRemove(key, out _);
        else _disabled[key] = true;
    }

    public void DeleteAccount(string adapterId, string accountId)
    {
        var key = AccountKey(adapterId, accountId);
        _deleted[key] = true;
        _disabled[key] = true;

        lock (_gate)
        {
            if (_manual.TryGetValue(adapterId, out var list))
            {
                var filtered = list.Where(a => !a.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase)).ToList();
                _manual[adapterId] = filtered;
            }
        }
    }

    public void MoveOrder(string adapterId, string accountId, int direction)
    {
        var accounts = AllAccountsOf(adapterId).ToList();
        var idx = accounts.FindIndex(a => a.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;
        var targetIdx = idx + direction;
        if (targetIdx < 0 || targetIdx >= accounts.Count) return;

        var current = accounts[idx];
        var target = accounts[targetIdx];

        var curKey = AccountKey(adapterId, current.AccountId);
        var tgtKey = AccountKey(adapterId, target.AccountId);

        // 交换顺序
        var curOrder = current.Order;
        var tgtOrder = target.Order;
        if (curOrder == tgtOrder)
        {
            curOrder = idx;
            tgtOrder = targetIdx;
        }

        _customOrder[curKey] = tgtOrder;
        _customOrder[tgtKey] = curOrder;
    }

    public void UpdateCredit(string adapterId, string accountId, int? credits)
    {
        var key = AccountKey(adapterId, accountId);
        if (credits.HasValue) _credits[key] = credits.Value;
        else _credits.TryRemove(key, out _);
    }

    private static string AccountKey(string adapterId, string accountId) => $"{adapterId}:{accountId}";

    public int TotalCount => _discovered.Values.Sum(v => v.Count) + _manual.Values.Sum(v => v.Count);

    public int ManualCount => _manual.Values.Sum(v => v.Count);
}
