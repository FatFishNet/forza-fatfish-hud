using ForzaOverlay.Core.Telemetry;

namespace ForzaOverlay.App.Rendering;

/// <summary>
/// 无增压车辆检测：游戏对自然吸气车同样会填充 Boost 字段，且数值会漂移，
/// 直接当真实增压会让增压表指针乱摆。判定依据是"踩油门时是否建立正压"——
/// 自吸车无论怎么踩都不会出现稳定的正压，增压车则必然建立。
/// 判定为无增压后，绘制方按各自约定表现（NFS19 归中、GHOST 显示 0.00）。
/// </summary>
internal sealed class BoostPresence
{
    private const float ThrottleThreshold = 0.4f;
    private const float PositiveBoostPsi = 0.5f;
    private const long ConfirmMs = 1500;
    private const long IdleResetMs = 5000;

    private int _carOrdinal = int.MinValue;
    private long _throttleMs;
    private long _noBoostMs;
    private long _lastTickMs;
    private bool _hasBoost;
    private bool _decided;

    /// <summary>当前车辆是否确认有增压。未判定前按"有"处理，避免起步瞬间闪变。</summary>
    internal bool HasBoost => !_decided || _hasBoost;

    /// <summary>是否为已知的无增压车辆。</summary>
    internal bool IsConfirmedAbsent => _decided && !_hasBoost;

    internal void Update(in ForzaDataPacket packet, long tickMs)
    {
        if (packet.CarOrdinal != _carOrdinal)
        {
            // 换车：重置所有判定状态
            _carOrdinal = packet.CarOrdinal;
            _throttleMs = 0;
            _noBoostMs = 0;
            _lastTickMs = tickMs;
            _hasBoost = false;
            _decided = false;
            return;
        }

        long delta = _lastTickMs > 0 ? Math.Clamp(tickMs - _lastTickMs, 0, 100) : 0;
        _lastTickMs = tickMs;

        if (!packet.RaceOn || packet.EngineMaxRpm <= 1f)
        {
            return;
        }

        float psi = packet.BoostPsi;

        if (psi > PositiveBoostPsi)
        {
            // 建立过正压就是增压车，不再回退
            _hasBoost = true;
            _decided = true;
            _noBoostMs = 0;
            return;
        }

        if (_decided)
        {
            return;
        }

        // 只在"踩油门且转速已起来"时累计无增压时间，滑行与怠速不算证据
        bool underLoad = packet.ThrottleNormalized >= ThrottleThreshold
            && packet.CurrentEngineRpm > packet.EngineIdleRpm * 1.5f;

        if (!underLoad)
        {
            _noBoostMs = Math.Max(0, _noBoostMs - delta);
            return;
        }

        _throttleMs += delta;
        _noBoostMs += delta;

        // 踩了足够久的油门都没建立正压，判定为无增压
        if (_noBoostMs >= ConfirmMs)
        {
            _decided = true;
            _hasBoost = false;
        }

        // 长时间没有负载，清掉累计的证据
        if (_throttleMs > IdleResetMs && _noBoostMs >= ConfirmMs)
        {
            _throttleMs = 0;
        }
    }
}
