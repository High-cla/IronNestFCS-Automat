using System.Collections;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public class Turret {
    private TurretController? _turret;


    public bool TryBind() {
        var turretObj = GameObject.Find("TurretSystem");
        if (turretObj == null) {
            MelonLogger.Error("[FCS] Aiming: Can't find TurretSystem");
            return false;
        }
        _turret = turretObj.GetComponent<TurretController>();
        return true;
    }
    /// <summary>最近一次 SetDesiredRotation 的目标角度，同优先级任务按此排序以减少转动</summary>
    public float LastSetAngle { get; private set; }

    /// <summary>非阻塞: 直接设目标方位角, 不等待转完。连续跟踪每帧调用。</summary>
    public void SetDesiredRotation(float angle) {
        if (_turret == null) return;
        _turret.DesiredRotation = -angle;
        LastSetAngle = angle;
    }

    /// <summary>就绪判定: 转到位(rotationVelocity==0)后与目标方位角的差(0-180)。未转完返回 180。</summary>
    public float AngleError(float targetAngle) {
        if (_turret == null) return 0f;
        if (_turret.rotationVelocity != 0f) return 180f;   // 仍在转 → 未就绪
        float d = Mathf.Abs(_turret.DesiredRotation + targetAngle) % 360f;
        return d > 180f ? 360f - d : d;
    }

}