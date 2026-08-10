using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>任务来源: 雷达自动 or 玩家手动(手动任务不被自动清队列清掉)</summary>
public enum TaskSource { Auto, Manual }

/// <summary>
/// 人机协同注册表: 统一管理"某个目标已被谁承包"。
/// 手动/自动任务共用: 入队或派发时登记, 击杀确认(Reconcile)或窗口到期解除。
/// 纯数据模块, 无 IL2CPP 引用, 热重载安全(Dispose 时 Clear)。
/// </summary>
public sealed class TargetRegistry
{
    /// <summary>击发后在飞窗口: 覆盖至多 ~60s 飞行 + 余量。打中由 Reconcile 提前解除。</summary>
    public const float FlightWindow = 65f;
    /// <summary>手动标记解析半径(km): 标记世界坐标附近该距离内的存活敌目标视为手动开火目标</summary>
    public const float ManualResolveMaxDistance = 1.0f;
    /// <summary>位置提交(解析失败)的屏蔽半径(km)</summary>
    private const float PositionCommitRadius = 0.3f;

    private readonly Dictionary<string, Entry> _byEntity = new();
    private readonly List<Entry> _positions = new();

    private sealed class Entry
    {
        public readonly ArtilleryTask Owner;
        public readonly string? EntityId;
        public readonly Vector3 Pos;
        public bool Fired;
        public float FiredAt;

        public Entry(ArtilleryTask owner, string? entityId, Vector3 pos)
        {
            Owner = owner;
            EntityId = entityId;
            Pos = pos;
        }

        public bool IsExpired => Fired && Time.time - FiredAt > FlightWindow;
    }

    /// <summary>登记目标。自动任务在派发时调用; 手动任务在入队时调用(队列存活不被清)。幂等。</summary>
    public void Commit(ArtilleryTask task)
    {
        if (task.entityId is { Length: > 0 } id)
            _byEntity[id] = new Entry(task, id, task.position);
        else
            _positions.Add(new Entry(task, null, task.position));
    }

    /// <summary>击发后调用: 启动飞行窗口计时。</summary>
    public void MarkFired(ArtilleryTask task)
    {
        if (Find(task) is { } e) { e.Fired = true; e.FiredAt = Time.time; }
    }

    /// <summary>任务结束(未击发)时解除登记。幂等。</summary>
    public void Release(ArtilleryTask task)
    {
        var e = Find(task);
        if (e == null) return;
        if (e.EntityId is { Length: > 0 } id) _byEntity.Remove(id);
        else _positions.Remove(e);
    }

    /// <summary>该目标是否已被承包(在飞/排队/炮上)。过期条目懒清理。</summary>
    public bool IsHandled(string entityId)
    {
        if (!_byEntity.TryGetValue(entityId, out var e)) return false;
        if (e.IsExpired) { _byEntity.Remove(entityId); return false; }
        return true;
    }

    /// <summary>该位置附近(含提交半径)是否已有位置提交。过期条目懒清理。</summary>
    public bool IsHandledNear(Vector3 pos, float radius)
    {
        foreach (var e in _positions)
        {
            if (e.IsExpired) continue;
            if (Vector3.Distance(pos, e.Pos) <= radius + PositionCommitRadius) return true;
        }
        _positions.RemoveAll(p => p.IsExpired);
        return false;
    }

    /// <summary>
    /// 击杀确认: 每次雷达 Scan 后调用。已确认死亡(不在存活集)的登记立即解除——
    /// 打中的目标下一轮扫描即恢复可拣, 只有打偏才等窗口到期。
    /// 击发过的目标被确认死亡时记录飞行时间日志(实测 ToF 数据采集)。
    /// </summary>
    public void Reconcile(List<string> aliveIds)
    {
        var alive = new HashSet<string>(aliveIds);
        foreach (var kv in _byEntity.ToList())
        {
            var e = kv.Value;
            if (e.IsExpired) { _byEntity.Remove(kv.Key); continue; }
            if (!alive.Contains(kv.Key))
            {
                _byEntity.Remove(kv.Key);
                if (e.Fired)
                    MelonLogger.Msg($"[FCS] Target '{kv.Key}' killed in flight ~{Time.time - e.FiredAt:F0}s (est.)");
            }
        }
        _positions.RemoveAll(p => p.IsExpired);
    }

    /// <summary>热重载/卸载时清空。</summary>
    public void Clear()
    {
        _byEntity.Clear();
        _positions.Clear();
    }

    private Entry? Find(ArtilleryTask task)
    {
        foreach (var e in _byEntity.Values)
            if (e.Owner == task) return e;
        foreach (var e in _positions)
            if (e.Owner == task) return e;
        return null;
    }
}
