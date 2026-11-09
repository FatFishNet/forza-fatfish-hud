namespace ForzaOverlay.Core.Configuration;

public sealed class HudLayoutProfile
{
    public HudModuleLayout PerformanceLayout { get; set; } = HudLayoutDefaults.PerformanceDefault();

    public HudModuleLayout FestivalLayout { get; set; } = HudLayoutDefaults.FestivalDefault();

    public HudModuleLayout ShiftTreeLayout { get; set; } = HudLayoutDefaults.ShiftTreeDefault();

    public HudModuleLayout Nfs19Layout { get; set; } = HudLayoutDefaults.Nfs19Default();

    public HudModuleLayout TireLayout { get; set; } = HudLayoutDefaults.TireDefault();

    public HudModuleLayout PerfLayout { get; set; } = HudLayoutDefaults.PerfDefault();

    public HudModuleLayout ShiftHintRgb3Layout { get; set; } = HudLayoutDefaults.ShiftHintRgb3Default();
}
