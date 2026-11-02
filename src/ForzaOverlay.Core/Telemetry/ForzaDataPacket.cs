using System.Runtime.InteropServices;

namespace ForzaOverlay.Core.Telemetry;

[StructLayout(LayoutKind.Explicit, Size = ForzaDataPacket.PacketSize)]
public struct ForzaDataPacket
{
    public const int PacketSize = 324;

    [FieldOffset(0)] public int IsRaceOn;
    [FieldOffset(4)] public uint TimestampMs;

    [FieldOffset(8)] public float EngineMaxRpm;
    [FieldOffset(12)] public float EngineIdleRpm;
    [FieldOffset(16)] public float CurrentEngineRpm;

    [FieldOffset(20)] public float AccelerationX;
    [FieldOffset(24)] public float AccelerationY;
    [FieldOffset(28)] public float AccelerationZ;

    [FieldOffset(32)] public float VelocityX;
    [FieldOffset(36)] public float VelocityY;
    [FieldOffset(40)] public float VelocityZ;

    [FieldOffset(44)] public float AngularVelocityX;
    [FieldOffset(48)] public float AngularVelocityY;
    [FieldOffset(52)] public float AngularVelocityZ;

    [FieldOffset(56)] public float Yaw;
    [FieldOffset(60)] public float Pitch;
    [FieldOffset(64)] public float Roll;

    [FieldOffset(68)] public float NormalizedSuspensionTravelFrontLeft;
    [FieldOffset(72)] public float NormalizedSuspensionTravelFrontRight;
    [FieldOffset(76)] public float NormalizedSuspensionTravelRearLeft;
    [FieldOffset(80)] public float NormalizedSuspensionTravelRearRight;

    [FieldOffset(84)] public float TireSlipRatioFrontLeft;
    [FieldOffset(88)] public float TireSlipRatioFrontRight;
    [FieldOffset(92)] public float TireSlipRatioRearLeft;
    [FieldOffset(96)] public float TireSlipRatioRearRight;

    [FieldOffset(100)] public float WheelRotationSpeedFrontLeft;
    [FieldOffset(104)] public float WheelRotationSpeedFrontRight;
    [FieldOffset(108)] public float WheelRotationSpeedRearLeft;
    [FieldOffset(112)] public float WheelRotationSpeedRearRight;

    [FieldOffset(116)] public int WheelOnRumbleStripFrontLeft;
    [FieldOffset(120)] public int WheelOnRumbleStripFrontRight;
    [FieldOffset(124)] public int WheelOnRumbleStripRearLeft;
    [FieldOffset(128)] public int WheelOnRumbleStripRearRight;

    [FieldOffset(132)] public int WheelInPuddleFrontLeft;
    [FieldOffset(136)] public int WheelInPuddleFrontRight;
    [FieldOffset(140)] public int WheelInPuddleRearLeft;
    [FieldOffset(144)] public int WheelInPuddleRearRight;

    [FieldOffset(148)] public float SurfaceRumbleFrontLeft;
    [FieldOffset(152)] public float SurfaceRumbleFrontRight;
    [FieldOffset(156)] public float SurfaceRumbleRearLeft;
    [FieldOffset(160)] public float SurfaceRumbleRearRight;

    [FieldOffset(164)] public float TireSlipAngleFrontLeft;
    [FieldOffset(168)] public float TireSlipAngleFrontRight;
    [FieldOffset(172)] public float TireSlipAngleRearLeft;
    [FieldOffset(176)] public float TireSlipAngleRearRight;

    [FieldOffset(180)] public float TireCombinedSlipFrontLeft;
    [FieldOffset(184)] public float TireCombinedSlipFrontRight;
    [FieldOffset(188)] public float TireCombinedSlipRearLeft;
    [FieldOffset(192)] public float TireCombinedSlipRearRight;

    [FieldOffset(196)] public float SuspensionTravelMetersFrontLeft;
    [FieldOffset(200)] public float SuspensionTravelMetersFrontRight;
    [FieldOffset(204)] public float SuspensionTravelMetersRearLeft;
    [FieldOffset(208)] public float SuspensionTravelMetersRearRight;

    [FieldOffset(212)] public int CarOrdinal;
    [FieldOffset(216)] public int CarClass;
    [FieldOffset(220)] public int CarPerformanceIndex;
    [FieldOffset(224)] public int DrivetrainType;
    [FieldOffset(228)] public int NumCylinders;

    [FieldOffset(232)] public uint CarGroup;
    [FieldOffset(236)] public float SmashableVelDiff;
    [FieldOffset(240)] public float SmashableMass;

    [FieldOffset(244)] public float PositionX;
    [FieldOffset(248)] public float PositionY;
    [FieldOffset(252)] public float PositionZ;

    [FieldOffset(256)] public float Speed;
    [FieldOffset(260)] public float Power;
    [FieldOffset(264)] public float Torque;

    [FieldOffset(268)] public float TireTempFrontLeft;
    [FieldOffset(272)] public float TireTempFrontRight;
    [FieldOffset(276)] public float TireTempRearLeft;
    [FieldOffset(280)] public float TireTempRearRight;

    [FieldOffset(284)] public float Boost;
    [FieldOffset(288)] public float Fuel;
    [FieldOffset(292)] public float DistanceTraveled;

    [FieldOffset(296)] public float BestLap;
    [FieldOffset(300)] public float LastLap;
    [FieldOffset(304)] public float CurrentLap;
    [FieldOffset(308)] public float CurrentRaceTime;

    [FieldOffset(312)] public ushort LapNumber;
    [FieldOffset(314)] public byte RacePosition;

    [FieldOffset(315)] public byte Accel;
    [FieldOffset(316)] public byte Brake;
    [FieldOffset(317)] public byte Clutch;
    [FieldOffset(318)] public byte HandBrake;
    [FieldOffset(319)] public byte Gear;
    [FieldOffset(320)] public sbyte Steer;
    [FieldOffset(321)] public sbyte NormalizedDrivingLine;
    [FieldOffset(322)] public sbyte NormalizedAiBrakeDifference;

    public const float MetersPerSecondToKmh = 3.6f;
    public const float PsiToBar = 0.0689476f;
    public const float WattsToKw = 0.001f;
    public const float WattsToHp = 0.001341022f;

    public readonly bool RaceOn => IsRaceOn == 1;
    public readonly float SpeedKmh => Speed * MetersPerSecondToKmh;
    public readonly float PowerKw => Power * WattsToKw;
    public readonly float PowerHp => Power * WattsToHp;
    public readonly float TorqueNm => Torque;
    public readonly float BoostPsi => Boost;
    public readonly float BoostBar => Boost * PsiToBar;
    public readonly float FuelFraction => Fuel;
    public readonly float ThrottleNormalized => Accel / 255f;
    public readonly float BrakeNormalized => Brake / 255f;
    public readonly float ClutchNormalized => Clutch / 255f;
    public readonly float HandBrakeNormalized => HandBrake / 255f;
    public readonly float SteerNormalized => Math.Clamp(Steer / 127f, -1f, 1f);
    public readonly int GearNumber => Gear switch
    {
        0 => -1,
        _ => Gear,
    };
}
