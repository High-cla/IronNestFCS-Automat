using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public enum Progress {
    Pending,
    Calculating,
    SelectingBullet,
    LoadingBullet,
    LoadingPowder,
    WaitLoading,
    Aiming,
    WaitingForFire,
    BackToIdle,
    Finished,
    Failed,
    Canceled,
}

public class ArtilleryTask {
    public int targetId;
    public string entityId = "";   // MapEntity key for dedup
    public float angel;
    public float distance;
    public Vector3 position;
    public BulletType bulletType;
    public Progress progress;
    /// <summary>为 true 时强制满装药量(6包)，覆盖用户全局 maxCharge 设置</summary>
    public bool useMaxCharge;
    /// <summary>切手动后置位:已开始装填的任务必须发射出去(原子化),击发段无视 AutoFire 自动开火</summary>
    public bool forceFire;
    /// <summary>任务来源: 雷达自动 or 玩家手动(手动任务不被自动清队列清掉)</summary>
    public TaskSource Source = TaskSource.Auto;
    /// <summary>切手动时置位: 未开始装填的自动任务干净放弃, 不碰炮膛</summary>
    public bool Canceled;
    /// <summary>已击发(Registry 飞行窗口计时依据; 未击发的任务结束时 Release 登记)</summary>
    public bool Fired;
}