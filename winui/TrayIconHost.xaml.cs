using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using System;
using System.Windows.Input;

namespace MonitorTune;

public sealed partial class TrayIconHost : UserControl
{
    public ICommand LeftClick { get; }

    public event Action? OnOpen;
    public event Action? OnExit;
    public event Action? OnAbout;
    public event Action? OnRefresh;

    public TrayIconHost()
    {
        InitializeComponent();
        LeftClick = new Relay(() => OnOpen?.Invoke());
        AutoStartItem.IsChecked = IsAutostart();
        Tray.ForceCreate();
        // Pre-warm меню: заставить layout pass отработать для ContextFlyout,
        // чтобы при первом реальном правом клике ширина уже была корректной.
        Loaded += (_, _) =>
        {
            try
            {
                if (Tray.ContextFlyout is MenuFlyout mf)
                {
                    foreach (var item in mf.Items)
                    {
                        if (item is FrameworkElement fe)
                            fe.Measure(new Windows.Foundation.Size(360, 100));
                    }
                }
            }
            catch { }
        };
    }

    void OpenClick(object sender, RoutedEventArgs e) => OnOpen?.Invoke();
    void ExitClick(object sender, RoutedEventArgs e) => OnExit?.Invoke();
    void AboutClick(object sender, RoutedEventArgs e) => OnAbout?.Invoke();
    void RefreshClick(object sender, RoutedEventArgs e) => OnRefresh?.Invoke();

    UpdateService.UpdateInfo? _pendingUpdate;
    /// <summary>Последняя известная UpdateInfo — используется toast handler'ом чтобы не
    /// re-CheckAsync (иначе получим второй UpdateAvailable event → дубликат toast).</summary>
    public UpdateService.UpdateInfo? PendingUpdate => _pendingUpdate;
    /// <summary>Показать в трее что доступно обновление (через баллун и активацию пункта меню).</summary>
    public void ShowUpdateAvailable(UpdateService.UpdateInfo info)
    {
        _pendingUpdate = info;
        try
        {
            if (UpdateMenuItem != null)
            {
                UpdateMenuItem.Text = $"Обновить до {info.Version}";
                UpdateMenuItem.Visibility = Visibility.Visible;
            }
            // Microsoft.Windows.AppNotifications — единственный путь для WinUI 3 desktop MSIX
            // где toast click правильно доставляется приложению через NotificationInvoked event.
            // Classic Windows.UI.Notifications требует ComServer + [ComVisible] + CLSID активатор;
            // AppNotificationManager делает это автоматически.
            var builder = new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder()
                .AddText($"Доступно обновление MoniTune {info.Version}")
                .AddText(info.Notes ?? "Нажмите чтобы установить обновление")
                .AddArgument("action", "update")
                .AddArgument("version", info.Version)
                .AddButton(new Microsoft.Windows.AppNotifications.Builder.AppNotificationButton("Обновить")
                    .AddArgument("action", "update")
                    .AddArgument("version", info.Version))
                .AddButton(new Microsoft.Windows.AppNotifications.Builder.AppNotificationButton("Позже")
                    .AddArgument("action", "dismiss"));
            var notification = builder.BuildNotification();
            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex) { App.LogStatic("ShowUpdateAvailable ex: " + ex.Message); }
    }

    public void ShowError(string message)
    {
        try
        {
            var builder = new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder()
                .AddText("MoniTune")
                .AddText(message);
            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex) { App.LogStatic("ShowError ex: " + ex.Message); }
    }

    async void UpdateClick(object sender, RoutedEventArgs e)
    {
        var info = _pendingUpdate;
        if (info == null) return;
        try
        {
            App.LogStatic($"User clicked update → {info.Version}");
            bool ok = await UpdateService.DownloadAndInstallAsync(info);
            if (ok) App.LogStatic("Update installed — приложение должно перезапуститься");
        }
        catch (Exception ex) { App.LogStatic("UpdateClick ex: " + ex); }
    }
    void AutoStartClick(object sender, RoutedEventArgs e)
    {
        SetAutostart(AutoStartItem.IsChecked);
    }

    // Автозапуск через StartupTask API (правильный путь для MSIX-приложений).
    // Объявлен в Package.appxmanifest как windows.startupTask с TaskId="MonitorTuneStartup".
    const string StartupTaskId = "MonitorTuneStartup";

    static bool IsAutostart()
    {
        try
        {
            var task = Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId).AsTask().GetAwaiter().GetResult();
            return task.State == Windows.ApplicationModel.StartupTaskState.Enabled
                || task.State == Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
        }
        catch { return false; }
    }
    static void SetAutostart(bool on)
    {
        try
        {
            var task = Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId).AsTask().GetAwaiter().GetResult();
            if (on)
                _ = task.RequestEnableAsync().AsTask().GetAwaiter().GetResult();
            else
                task.Disable();
        }
        catch { }
    }

    sealed class Relay : ICommand
    {
        readonly Action _act;
        public Relay(Action a) { _act = a; }
        public bool CanExecute(object? p) => true;
        public void Execute(object? p) => _act();
        public event EventHandler? CanExecuteChanged;
    }
}
