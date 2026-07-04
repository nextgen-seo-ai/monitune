using System.Management;
using System.Runtime.InteropServices;

namespace MonitorTune;

// Определение GPU-вендора для каждого монитора.
// Разные драйверы имеют разное поведение DDC:
//   Intel:      кеширует MCCS, задержка перед Get-verify ~300мс
//   AMD:        агрессивно кеширует, ~500мс
//   NVIDIA:     не кеширует, ~200мс, но роняет DDC на 2-10с после WM_DISPLAYCHANGE
//   DisplayLink: НЕ передаёт DDC вообще (USB video, не настоящий DP/HDMI)
public static class GpuDetector
{
    /// <summary>Определить GPU для конкретного \\.\DISPLAY# через EnumDisplayDevices adapter string.</summary>
    public static (GpuVendor vendor, string? name) DetectForDisplay(string displayDevice)
    {
        try
        {
            var dd = new Native.DISPLAY_DEVICE { cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>() };
            // adapter = pass display name to EnumDisplayDevices with null iDevNum
            // Реально: адаптер получаем через iDevNum=0 на самом display device.
            uint i = 0;
            while (Native.EnumDisplayDevices(null!, i, ref dd, 0))
            {
                if (dd.DeviceName == displayDevice)
                {
                    var name = dd.DeviceString?.Trim() ?? "";
                    return (Classify(name), name);
                }
                dd.cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>();
                i++;
            }
        }
        catch { }
        return (GpuVendor.Unknown, null);
    }

    static GpuVendor Classify(string name)
    {
        if (string.IsNullOrEmpty(name)) return GpuVendor.Unknown;
        string s = name.ToLowerInvariant();
        if (s.Contains("displaylink") || s.Contains("indirect display")) return GpuVendor.DisplayLink;
        if (s.Contains("intel")) return GpuVendor.Intel;
        if (s.Contains("nvidia") || s.Contains("geforce") || s.Contains("quadro") || s.Contains("tesla")) return GpuVendor.Nvidia;
        if (s.Contains("amd") || s.Contains("radeon") || s.Contains("ati ")) return GpuVendor.Amd;
        if (s.Contains("qualcomm") || s.Contains("snapdragon") || s.Contains("adreno")) return GpuVendor.Qualcomm;
        if (s.Contains("microsoft basic") || s.Contains("microsoft remote")) return GpuVendor.Microsoft;
        return GpuVendor.Unknown;
    }

    /// <summary>Рекомендуемая задержка перед Get-verify (по данным workflow research).</summary>
    public static int VerifyDelayFor(GpuVendor gpu) => gpu switch
    {
        GpuVendor.Intel => 300,
        GpuVendor.Amd => 500,      // AMD агрессивно кеширует MCCS responses
        GpuVendor.Nvidia => 200,
        GpuVendor.Qualcomm => 250,
        _ => 300,
    };
}
