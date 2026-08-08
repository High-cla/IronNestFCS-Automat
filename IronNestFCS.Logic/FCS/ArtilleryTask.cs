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
}