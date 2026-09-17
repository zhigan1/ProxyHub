using System.Collections.Concurrent;

namespace ProxyHub;

/// <summary>
/// 熔断注册表：节点粒度 {适配器|账号|上游模型}。
/// Closed →（连续失败达阈值）→ Open（冷却期直接跳过）→（冷却期满放行一个探测请求）→ Half-Open
/// → 成功回 Closed / 失败续 Open（冷却期重新计时）。
/// 参数热更新（UpdateSettings）只改阈值与冷却时长，不清空既有状态。
/// </summary>
public sealed class CircuitBreakerRegistry
{
    public sealed record BreakerState(
        string Key, string State, int ConsecutiveFailures,
        DateTimeOffset? OpenedAt, string? LastError,
        int FailureThreshold, int CooldownSeconds);

    private sealed class Node
    {
        public int ConsecutiveFailures;
        public DateTimeOffset? OpenedAt;
        public string? LastError;
        public int Probing;
    }

    private readonly ConcurrentDictionary<string, Node> _nodes = new();
    private readonly Func<DateTimeOffset> _clock;
    private CircuitBreakerConfig _settings;

    public CircuitBreakerRegistry(CircuitBreakerConfig? settings = null, Func<DateTimeOffset>? clock = null)
    {
        _settings = settings ?? new CircuitBreakerConfig();
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
    }

    public void UpdateSettings(CircuitBreakerConfig cfg) => _settings = cfg;

    /// <summary>
    /// 是否放行该节点：Closed 恒放行；Open 冷却期内拒绝；冷却期满时
    /// （启用半开探测则）仅放行一个请求作探测，其余仍拒绝。
    /// </summary>
    public bool TryEnter(string key)
    {
        if (!_nodes.TryGetValue(key, out var node)) return true; // 无节点 = Closed，读路径不建节点
        lock (node)
        {
            if (node.OpenedAt is null) return true;

            var cooldown = TimeSpan.FromSeconds(Math.Max(1, _settings.CooldownSeconds));
            if (_clock() - node.OpenedAt.Value < cooldown) return false;

            if (!_settings.HalfOpenProbe)
            {
                node.OpenedAt = null;
                node.ConsecutiveFailures = 0;
                return true;
            }
            if (Interlocked.CompareExchange(ref node.Probing, 1, 0) == 1) return false; // 已有探测在途
            return true;
        }
    }

    public void RecordSuccess(string key)
    {
        var node = _nodes.GetOrAdd(key, _ => new Node());
        lock (node)
        {
            node.ConsecutiveFailures = 0;
            node.OpenedAt = null;
            node.Probing = 0;
        }
    }

    public void RecordFailure(string key, string? error = null)
    {
        var node = _nodes.GetOrAdd(key, _ => new Node());
        lock (node)
        {
            node.ConsecutiveFailures++;
            node.LastError = error;
            node.Probing = 0;
            if (node.OpenedAt is not null || node.ConsecutiveFailures >= Math.Max(1, _settings.FailureThreshold))
                node.OpenedAt = _clock(); // 首次达阈值 → Open；半开探测失败 → 重新计时
        }
    }

    public void Reset(string key) => _nodes.TryRemove(key, out _);

    public void ResetAll() => _nodes.Clear();

    private string StateOf(Node node)
    {
        lock (node)
        {
            if (node.OpenedAt is null) return "closed";
            var cooldown = TimeSpan.FromSeconds(Math.Max(1, _settings.CooldownSeconds));
            return _clock() - node.OpenedAt.Value >= cooldown ? "half-open" : "open";
        }
    }

    public IReadOnlyList<BreakerState> Snapshot() =>
        _nodes.Select(kv =>
        {
            var (key, node) = kv;
            lock (node)
            {
                return new BreakerState(
                    key, StateOf(node), node.ConsecutiveFailures,
                    node.OpenedAt, node.LastError,
                    _settings.FailureThreshold, _settings.CooldownSeconds);
            }
        }).ToList();
}
