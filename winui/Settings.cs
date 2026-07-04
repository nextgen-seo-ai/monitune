using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorTune;

// Все настройки приложения. Сериализуется в LocalAppData/MonitorTune/settings.json.
public class Settings
{
    public bool SyncAllMonitors { get; set; }
    public Dictionary<string, MonitorSettings> Monitors { get; set; } = new();
    public NightModeSettings NightMode { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public int StepSize { get; set; } = 5;
    public KeepAwakeSettings KeepAwake { get; set; } = new();

    /// <summary>URL'ы источников обновлений. Primary — GitHub Releases (public, TLS+CDN).
    /// Fallback-URL'ы (приватные mesh-узлы) НЕ хранятся здесь — они зашиты в UpdateService compile-time
    /// и никогда не сериализуются в открытый settings.json. См. UpdateService.PrivateFallbackManifests.</summary>
    public List<string> UpdateManifestUrls { get; set; } = new();

    /// <summary>Автоматически проверять обновления при старте (silent check + tray notification).</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>Отправлять анонимные crash-репорты и диагностические данные (opt-in). По умолчанию выключено.</summary>
    public bool TelemetryEnabled { get; set; } = false;

    /// <summary>HTTPS endpoint для приёма crash-репортов. Если пусто — только локальный crashes/*.json.</summary>
    public string? TelemetryEndpoint { get; set; }

    /// <summary>Разрешить установку версии меньше текущей (downgrade). По умолчанию false — защита от отката на уязвимую сборку.
    /// Намеренно не выводится в UI: юзер должен явно править settings.json, чтобы случайно не выключить защиту.</summary>
    public bool AllowDowngrade { get; set; } = false;
}

public class KeepAwakeSettings
{
    /// <summary>Не давать компьютеру и экрану засыпать (SetThreadExecutionState).</summary>
    public bool PreventSleep { get; set; }

    /// <summary>Имитация активности — невидимое движение мыши раз в N секунд.
    /// Показывает пользователя онлайн в Teams/Slack/Discord/любых статус-приложениях.</summary>
    public bool SimulateActivity { get; set; }

    /// <summary>Интервал имитации в секундах.</summary>
    public int IntervalSec { get; set; } = 30;

    /// <summary>Видимое движение курсора (1 пиксель туда-обратно).
    /// По умолчанию выключено — невидимое движение тоже считается активностью,
    /// но не мешает пользователю.</summary>
    public bool VisibleMove { get; set; }
}

public class MonitorSettings
{
    public bool LinkBrightnessContrast { get; set; }
    public int DayBrightness { get; set; } = -1;
    public int DayContrast { get; set; } = -1;
}

public class NightModeSettings
{
    public bool ScheduleEnabled { get; set; }
    public string StartTime { get; set; } = "22:00";
    public string EndTime { get; set; } = "07:00";
    public int NightBrightness { get; set; } = 15;
    public int NightContrast { get; set; } = 40;
    public bool IsActive { get; set; }
}

public class HotkeySettings
{
    // PgUp=0x21, PgDn=0x22 — не конфликтуют с GPU rotation хоткеями на Ctrl+Alt+Стрелки.
    public Hotkey BrightnessUp { get; set; } = new() { Mod = HotkeyMod.Ctrl | HotkeyMod.Alt, Key = 0x21 };
    public Hotkey BrightnessDown { get; set; } = new() { Mod = HotkeyMod.Ctrl | HotkeyMod.Alt, Key = 0x22 };
    public Hotkey ContrastUp { get; set; } = new() { Mod = HotkeyMod.Ctrl | HotkeyMod.Alt | HotkeyMod.Shift, Key = 0x21 };
    public Hotkey ContrastDown { get; set; } = new() { Mod = HotkeyMod.Ctrl | HotkeyMod.Alt | HotkeyMod.Shift, Key = 0x22 };
    public Hotkey ToggleNightMode { get; set; } = new() { Mod = HotkeyMod.Ctrl | HotkeyMod.Alt, Key = 0x4E };
    public bool Enabled { get; set; } = true;
}

[Flags]
public enum HotkeyMod { None = 0, Alt = 1, Ctrl = 2, Shift = 4, Win = 8 }

public class Hotkey
{
    public HotkeyMod Mod { get; set; }
    public int Key { get; set; }

    [JsonIgnore]
    public bool IsValid => Key != 0;
}

public static class SettingsStore
{
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MonitorTune");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static Settings Current { get; private set; } = new();
    public static event Action? Changed;

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var txt = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<Settings>(txt);
                if (s != null) Current = s;
            }
        }
        catch { }

        // Миграция старых конфликтующих хоткеев Ctrl+Alt+Стрелки → PgUp/PgDn.
        MigrateConflictingHotkeys();
    }

    static void MigrateConflictingHotkeys()
    {
        var h = Current.Hotkeys;
        bool changed = false;
        const int VK_UP = 0x26, VK_DOWN = 0x28, VK_PGUP = 0x21, VK_PGDN = 0x22;
        if (h.BrightnessUp.Key == VK_UP) { h.BrightnessUp.Key = VK_PGUP; changed = true; }
        if (h.BrightnessDown.Key == VK_DOWN) { h.BrightnessDown.Key = VK_PGDN; changed = true; }
        if (h.ContrastUp.Key == VK_UP) { h.ContrastUp.Key = VK_PGUP; changed = true; }
        if (h.ContrastDown.Key == VK_DOWN) { h.ContrastDown.Key = VK_PGDN; changed = true; }
        if (changed) Save();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var txt = JsonSerializer.Serialize(Current, JsonOpts);
            File.WriteAllText(FilePath, txt);
            Changed?.Invoke();
        }
        catch { }
    }

    public static MonitorSettings GetOrCreate(string token)
    {
        if (string.IsNullOrEmpty(token)) token = "default";
        if (!Current.Monitors.TryGetValue(token, out var ms))
        {
            ms = new MonitorSettings();
            Current.Monitors[token] = ms;
        }
        return ms;
    }
}
