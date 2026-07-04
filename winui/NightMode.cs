using Microsoft.UI.Dispatching;
using System.Diagnostics;

namespace MonitorTune;

public class NightMode
{
    readonly DdcManager ddc;
    readonly DispatcherQueue ui;
    System.Threading.Timer? scheduleTimer;
    bool lastScheduledState;

    public event Action? StateChanged;

    public NightMode(DdcManager ddc, DispatcherQueue ui)
    {
        this.ddc = ddc;
        this.ui = ui;
        StartScheduleTimer();
    }

    public bool IsActive => SettingsStore.Current.NightMode.IsActive;

    public void Toggle()
    {
        if (IsActive) Deactivate();
        else Activate();
    }

    /// <summary>Включить ночной режим: сохранить дневные значения, применить ночные.</summary>
    public void Activate()
    {
        if (IsActive) return;
        var ns = SettingsStore.Current.NightMode;

        foreach (var m in ddc.Monitors)
        {
            var ms = SettingsStore.GetOrCreate(m.Token ?? "");
            if (m.Brightness >= 0) ms.DayBrightness = m.Brightness;
            if (m.Contrast >= 0) ms.DayContrast = m.Contrast;

            ddc.Request(IndexOf(m), DdcManager.VCP_BRIGHTNESS, ns.NightBrightness);
            ddc.Request(IndexOf(m), DdcManager.VCP_CONTRAST, ns.NightContrast);
        }

        ns.IsActive = true;
        SettingsStore.Save();
        ui.TryEnqueue(() => StateChanged?.Invoke());
    }

    public void Deactivate()
    {
        if (!IsActive) return;
        foreach (var m in ddc.Monitors)
        {
            var ms = SettingsStore.GetOrCreate(m.Token ?? "");
            if (ms.DayBrightness >= 0) ddc.Request(IndexOf(m), DdcManager.VCP_BRIGHTNESS, ms.DayBrightness);
            if (ms.DayContrast >= 0) ddc.Request(IndexOf(m), DdcManager.VCP_CONTRAST, ms.DayContrast);
        }

        SettingsStore.Current.NightMode.IsActive = false;
        SettingsStore.Save();
        ui.TryEnqueue(() => StateChanged?.Invoke());
    }

    int IndexOf(MonInfo m) => ddc.Monitors.IndexOf(m);

    void StartScheduleTimer()
    {
        // Проверка расписания раз в минуту.
        scheduleTimer = new System.Threading.Timer(_ =>
        {
            try { CheckSchedule(); }
            catch (Exception ex) { Debug.WriteLine("Schedule check ex: " + ex); }
        }, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

    void CheckSchedule()
    {
        var ns = SettingsStore.Current.NightMode;
        if (!ns.ScheduleEnabled) return;

        if (!TimeOnly.TryParse(ns.StartTime, out var start)) return;
        if (!TimeOnly.TryParse(ns.EndTime, out var end)) return;

        var now = TimeOnly.FromDateTime(DateTime.Now);
        bool shouldBeNight = IsInRange(now, start, end);

        // Срабатываем только на изменение состояния расписания (чтобы не дёргать DDC каждую минуту).
        if (shouldBeNight == lastScheduledState && IsActive == shouldBeNight) return;
        lastScheduledState = shouldBeNight;

        if (shouldBeNight && !IsActive) ui.TryEnqueue(Activate);
        else if (!shouldBeNight && IsActive) ui.TryEnqueue(Deactivate);
    }

    static bool IsInRange(TimeOnly now, TimeOnly start, TimeOnly end)
    {
        // Если интервал переходит через полночь (например 22:00 → 07:00).
        if (start <= end) return now >= start && now < end;
        return now >= start || now < end;
    }

    public void Stop()
    {
        scheduleTimer?.Dispose();
        scheduleTimer = null;
    }
}
