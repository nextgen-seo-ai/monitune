using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace MonitorTune;

public sealed partial class SettingsWindow : Window
{
    bool loading = true;

    public SettingsWindow()
    {
        InitializeComponent();

        var hwnd = WindowNative.GetWindowHandle(this);
        var aw = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        // Учитываем DPI — иначе на 150% окно выходит 373x467 и контент обрезается.
        uint dpi = Native.GetDpiForWindow(hwnd);
        double scale = dpi / 96.0;
        aw.Resize(new Windows.Graphics.SizeInt32(
            (int)Math.Ceiling(620 * scale),
            (int)Math.Ceiling(760 * scale)));
        aw.IsShownInSwitchers = false;
        // IsAlwaysOnTop НЕ ставим — иначе Windows не шлёт Deactivated и автоскрытие не сработает.
        try { aw.SetIcon("Assets/AppIcon.ico"); } catch { }
        CenterOnDisplayWithCursor(aw);

        var s = SettingsStore.Current;
        StepSizeBox.Value = s.StepSize;
        NightBrightnessBox.Value = s.NightMode.NightBrightness;
        NightContrastBox.Value = s.NightMode.NightContrast;
        ScheduleSwitch.IsOn = s.NightMode.ScheduleEnabled;
        PreventSleepSwitch.IsOn = s.KeepAwake.PreventSleep;
        SimulateActivitySwitch.IsOn = s.KeepAwake.SimulateActivity;
        IntervalBox.Value = s.KeepAwake.IntervalSec;
        VisibleMoveSwitch.IsOn = s.KeepAwake.VisibleMove;
        AutoUpdateSwitch.IsOn = s.AutoCheckUpdates;
        TelemetrySwitch.IsOn = s.TelemetryEnabled;
        if (TimeOnly.TryParse(s.NightMode.StartTime, out var st))
            StartTimePicker.SelectedTime = new TimeSpan(st.Hour, st.Minute, 0);
        if (TimeOnly.TryParse(s.NightMode.EndTime, out var en))
            EndTimePicker.SelectedTime = new TimeSpan(en.Hour, en.Minute, 0);
        loading = false;

        // Закрывать окно при потере фокуса (типа Quick Settings).
        Activated += (s, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
                Close();
        };
    }

    void OnStepChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (loading || double.IsNaN(args.NewValue)) return;
        SettingsStore.Current.StepSize = (int)args.NewValue;
        SettingsStore.Save();
    }

    void OnNightValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (loading || double.IsNaN(args.NewValue)) return;
        if (sender == NightBrightnessBox)
            SettingsStore.Current.NightMode.NightBrightness = (int)args.NewValue;
        else if (sender == NightContrastBox)
            SettingsStore.Current.NightMode.NightContrast = (int)args.NewValue;
        SettingsStore.Save();
    }

    void OnScheduleToggled(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        SettingsStore.Current.NightMode.ScheduleEnabled = ScheduleSwitch.IsOn;
        SettingsStore.Save();
    }

    void OnTimeChanged(TimePicker sender, TimePickerSelectedValueChangedEventArgs args)
    {
        if (loading || args.NewTime == null) return;
        var t = args.NewTime.Value;
        var str = $"{t.Hours:00}:{t.Minutes:00}";
        if (sender == StartTimePicker)
            SettingsStore.Current.NightMode.StartTime = str;
        else
            SettingsStore.Current.NightMode.EndTime = str;
        SettingsStore.Save();
    }

    void OnPreventSleepToggled(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        SettingsStore.Current.KeepAwake.PreventSleep = PreventSleepSwitch.IsOn;
        SettingsStore.Save();
    }

    void OnSimulateActivityToggled(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        SettingsStore.Current.KeepAwake.SimulateActivity = SimulateActivitySwitch.IsOn;
        SettingsStore.Save();
    }

    void OnIntervalChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (loading || double.IsNaN(args.NewValue)) return;
        SettingsStore.Current.KeepAwake.IntervalSec = (int)args.NewValue;
        SettingsStore.Save();
    }

    void OnVisibleMoveToggled(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        SettingsStore.Current.KeepAwake.VisibleMove = VisibleMoveSwitch.IsOn;
        SettingsStore.Save();
    }

    void OnAutoUpdateToggled(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        SettingsStore.Current.AutoCheckUpdates = AutoUpdateSwitch.IsOn;
        SettingsStore.Save();
    }

    void OnTelemetryToggled(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        SettingsStore.Current.TelemetryEnabled = TelemetrySwitch.IsOn;
        SettingsStore.Save();
    }

    async void CheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        UpdateStatusText.Text = "Проверка…";
        try
        {
            var info = await UpdateService.CheckAsync();
            if (info == null)
            {
                UpdateStatusText.Text = "Установлена актуальная версия.";
            }
            else
            {
                UpdateStatusText.Text = $"Доступна версия {info.Version}. Уведомление в трее.";
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "Ошибка: " + ex.Message;
        }
    }

    void CloseClick(object sender, RoutedEventArgs e) => Close();

    static void CenterOnDisplayWithCursor(AppWindow aw)
    {
        Native.POINT pt;
        Native.GetCursorPos(out pt);
        var display = DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(pt.X, pt.Y), DisplayAreaFallback.Primary);
        var wa = display.WorkArea;
        int left = wa.X + (wa.Width - aw.Size.Width) / 2;
        int top = wa.Y + (wa.Height - aw.Size.Height) / 2;
        aw.Move(new Windows.Graphics.PointInt32(left, top));
    }
}
