using System.Linq;
using System.Runtime.InteropServices;

namespace MonitorTune;

// Получение точного имени монитора через fallback chain.
// Приоритет (первый успех — стоп):
//  1. DisplayConfigGetDeviceInfo(MONITOR_DEVICE_NAME) — если установлен vendor INF,
//     возвращает полное переопределённое имя ("Samsung U28R55" вместо "U28R55").
//  2. WMI WmiMonitorID.UserFriendlyName (это и есть EDID descriptor 0xFC).
//  3. EDID descriptor 0xFC из реестра (EdidReader).
//  4. Final fallback — "<Vendor> <Token>".
//
// Кеш per-monitor по device path, чтобы не дёргать DisplayConfig многократно.
public static class MonitorNameResolver
{
    static readonly Dictionary<string, string> _cache = new();

    public class Resolved
    {
        public string Name = "Монитор";
        public string Source = "fallback";
    }

    /// <summary>Получить лучшее имя монитора. devicePath — то что выдаёт EnumDisplayDevices DeviceID.</summary>
    public static Resolved Resolve(string? devicePath, string? token, string? edidName, string? wmiFriendly, string? vendorName, ushort productCode = 0)
    {
        // Cache hit
        if (!string.IsNullOrEmpty(devicePath) && _cache.TryGetValue(devicePath, out var cached))
            return new Resolved { Name = cached, Source = "cache" };

        // 1. DisplayConfig (vendor INF marketing name)
        if (!string.IsNullOrEmpty(devicePath))
        {
            try
            {
                var cfg = QueryDisplayConfigName(devicePath!);
                if (!string.IsNullOrWhiteSpace(cfg) && !IsGeneric(cfg))
                {
                    var name = Compose(vendorName, cfg!);
                    _cache[devicePath!] = name;
                    return new Resolved { Name = name, Source = "DisplayConfig" };
                }
            }
            catch { }
        }

        // 2. WMI UserFriendlyName
        if (!string.IsNullOrWhiteSpace(wmiFriendly) && !IsGeneric(wmiFriendly))
        {
            var name = Compose(vendorName, wmiFriendly!);
            if (!string.IsNullOrEmpty(devicePath)) _cache[devicePath!] = name;
            return new Resolved { Name = name, Source = "WMI" };
        }

        // 3. EDID descriptor 0xFC
        if (!string.IsNullOrWhiteSpace(edidName) && !IsGeneric(edidName))
        {
            var name = Compose(vendorName, edidName!);
            if (!string.IsNullOrEmpty(devicePath)) _cache[devicePath!] = name;
            return new Resolved { Name = name, Source = "EDID" };
        }

        // 4. Embedded БД моделей (linuxhw EDID, CC-BY-4.0)
        string? pnp = !string.IsNullOrEmpty(token) && token!.Length >= 3 ? token.Substring(0, 3) : null;
        if (pnp != null && productCode != 0)
        {
            var dbModel = MonitorDatabase.ModelByCode(pnp, productCode);
            if (!string.IsNullOrWhiteSpace(dbModel))
            {
                var v = vendorName ?? MonitorDatabase.VendorByPnp(pnp);
                var name = Compose(v, dbModel!);
                if (!string.IsNullOrEmpty(devicePath)) _cache[devicePath!] = name;
                return new Resolved { Name = name, Source = "Database" };
            }
        }

        // 5. Final fallback — vendor (из БД если есть) + token
        var fallbackVendor = vendorName ?? MonitorDatabase.VendorByPnp(pnp);
        var fallbackName = Compose(fallbackVendor, token ?? "Монитор");
        if (!string.IsNullOrEmpty(devicePath)) _cache[devicePath!] = fallbackName;
        return new Resolved { Name = fallbackName, Source = "fallback" };
    }

    static bool IsGeneric(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return true;
        var lower = s.Trim().ToLowerInvariant();
        return lower.Contains("generic pnp") || lower.Contains("default monitor") || lower == "monitor";
    }

    static string Compose(string? vendor, string nm)
    {
        if (string.IsNullOrEmpty(vendor)) return nm;
        // если имя уже начинается с вендора — не дублируем
        if (nm.StartsWith(vendor, StringComparison.OrdinalIgnoreCase)) return nm;
        return vendor + " " + nm;
    }

    /// <summary>Получить marketing-name через DisplayConfigGetDeviceInfo по device path.</summary>
    static string? QueryDisplayConfigName(string devicePath)
    {
        var (name, _) = QueryDisplayConfig(devicePath);
        return name;
    }

    /// <summary>Получить тип подключения монитора (DP/HDMI/USB-C и т.д.).</summary>
    public static OutputTech GetOutputTechnology(string? devicePath)
    {
        if (string.IsNullOrEmpty(devicePath)) return OutputTech.Unknown;
        var (_, tech) = QueryDisplayConfig(devicePath!);
        return tech;
    }

    static (string? name, OutputTech tech) QueryDisplayConfig(string devicePath)
    {
        int err = Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out uint numPath, out uint numMode);
        if (err != 0 || numPath == 0) return (null, OutputTech.Unknown);
        var paths = new Native.DISPLAYCONFIG_PATH_INFO[numPath];
        var modes = new Native.DISPLAYCONFIG_MODE_INFO[numMode];
        err = Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS, ref numPath, paths, ref numMode, modes, IntPtr.Zero);
        if (err != 0) return (null, OutputTech.Unknown);

        foreach (var p in paths.Take((int)numPath))
        {
            var nameInfo = new Native.DISPLAYCONFIG_TARGET_DEVICE_NAME();
            nameInfo.header.type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
            nameInfo.header.size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_TARGET_DEVICE_NAME>();
            nameInfo.header.adapterId = p.targetInfo.adapterId;
            nameInfo.header.id = p.targetInfo.id;
            if (Native.DisplayConfigGetDeviceInfo(ref nameInfo) != 0) continue;
            if (!PathsMatch(devicePath, nameInfo.monitorDevicePath ?? "")) continue;

            var friendly = string.IsNullOrWhiteSpace(nameInfo.monitorFriendlyDeviceName)
                ? null : nameInfo.monitorFriendlyDeviceName.Trim();
            var tech = MapOutputTechnology(p.targetInfo.outputTechnology);
            return (friendly, tech);
        }
        return (null, OutputTech.Unknown);
    }

    // DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY values (wingdi.h).
    static OutputTech MapOutputTechnology(uint code) => code switch
    {
        0 => OutputTech.Vga,
        1 => OutputTech.Other,          // S-Video
        2 => OutputTech.Other,          // Composite
        4 => OutputTech.Dvi,
        5 => OutputTech.Hdmi,
        6 => OutputTech.Other,          // LVDS
        8 => OutputTech.Other,          // D-Jpn
        9 => OutputTech.Other,          // SDI
        10 => OutputTech.DisplayPort,
        11 => OutputTech.DisplayPort,   // DP embedded (внутренний)
        12 => OutputTech.UsbC,          // UDI external
        13 => OutputTech.UsbC,          // UDI embedded
        14 => OutputTech.Other,         // SDTVDONGLE
        15 => OutputTech.Wireless,      // MIRACAST
        16 => OutputTech.UsbC,          // INDIRECT_WIRED
        17 => OutputTech.Wireless,      // INDIRECT_VIRTUAL
        18 => OutputTech.DpOverThunderbolt, // DP через USB4/Thunderbolt tunnel
        0x80000000 => OutputTech.Internal,
        _ => OutputTech.Unknown,
    };

    static bool PathsMatch(string enumDeviceId, string configDevicePath)
    {
        // Извлекаем "SAM1015" из обоих путей и сравниваем.
        if (string.IsNullOrEmpty(enumDeviceId) || string.IsNullOrEmpty(configDevicePath)) return false;
        var enumParts = enumDeviceId.Split('\\');
        if (enumParts.Length < 2) return false;
        string hwId = enumParts[1]; // SAM1015
        return configDevicePath.Contains("#" + hwId + "#", StringComparison.OrdinalIgnoreCase) ||
               configDevicePath.Contains("\\" + hwId + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
