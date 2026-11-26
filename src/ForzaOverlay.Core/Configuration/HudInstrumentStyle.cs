namespace ForzaOverlay.Core.Configuration;

public enum HudInstrumentStyle
{
    Classic = 0,
    Festival = 1,
    ShiftTree = 2,
    Nfs19 = 3,

    /// <summary>咬地鲨三表（Lexus Altezza / IS200 仪表集群）。</summary>
    Altezza = 4,

    /// <summary>ES 数字转速盘（单表转速 + 中央车速/档位，青色点阵）。</summary>
    Es = 5,

    /// <summary>极简弧环（FH4 极简语言：白色细弧 + 中央档位，超红区转紫并呼吸辉光）。</summary>
    Ring = 6,

    /// <summary>漂移锁链（FH5：紫→青渐变转速弧 + 红区斜纹，中央档位/车速 + 底部增压弧）。</summary>
    DriftChain = 7,
}
