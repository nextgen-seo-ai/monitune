using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using System;
using System.Windows.Input;

namespace MonitorTune;

public sealed partial class TrayIconHost : UserControl
{
    public ICommand LeftClick { get; }

    /// <summary>Открыть панель у tray icon (позиция курсора релевантна — левый клик по иконке).</summary>
    public event Action? OnOpen;
    /// <summary>Открыть панель через контекстное меню — курсор в пункте меню, нужна tray-позиция вместо cursor.</summary>
    public event Action? OnOpenFromMenu;
    public event Action? OnExit;
    public event Action? OnAbout;
    public event Action? OnRefresh;

    public TrayIconHost()
    {
        InitializeComponent();
        LeftClick = new Relay(() => OnOpen?.Invoke());
        AutoStartItem.IsChecked = IsAutostart();
        // Фиксированный GUID для tray icon — Windows tracks позицию иконки в трее
        // через NIF_GUID (по Guid), а не по пути exe. Без этого при каждом MSIX-update
        // путь exe меняется (WindowsApps\MonitorTune_X.X.X.X_...) → Windows считает
        // это новой иконкой и роняет её в overflow "стрелка вверх". Юзер должен
        // вручную перетаскивать после каждого обновления. С GUID — position preserved.
        Tray.Id = new Guid("CE7F9D62-89B4-4A4E-9D3A-4B7C5A2F1E6E");
        Tray.ForceCreate();
        // Pre-warm меню: MenuFlyoutPresenter создаётся только при первом ShowAt.
        // ContextMenuMode=SecondWindow: H.NotifyIcon сам вызывает ShowAt через свой popup host,
        // но первый вызов не имеет полного layout pass → меню обрезано в компактный размер.
        // Fix: сами делаем один ShowAt на off-screen позицию + сразу Hide. Presenter создастся,
        // MeasureOverride отработает, размер закэшируется. Реальный правый клик уже правильный.
        // ContextMenuMode=SecondWindow: H.NotifyIcon рендерит меню в отдельном popup window
        // со своим resource tree. Style на MenuFlyoutPresenter НЕ применяется первый раз —
        // поэтому явно ставим MinWidth на каждый item через code (это гарантирует что
        // presenter не может быть уже суммы items и текст не обрезается).
        const double ItemMinWidth = 320;
        OpenMenuItem.MinWidth = ItemMinWidth;
        RefreshMenuItem.MinWidth = ItemMinWidth;
        UpdateMenuItem.MinWidth = ItemMinWidth;
        AutoStartItem.MinWidth = ItemMinWidth;
        AboutMenuItem.MinWidth = ItemMinWidth;
        ExitMenuItem.MinWidth = ItemMinWidth;

        // WinUI 3 MenuFlyoutItem обрабатывает только левый клик. Юзеры трея часто
        // держат курсор на правой кнопке (правый клик открыл меню — пальцу удобно
        // тем же кликом выбрать пункт). Дублируем правый клик через RightTapped.
        HookRightClickAsLeft(OpenMenuItem, OpenClick);
        HookRightClickAsLeft(RefreshMenuItem, RefreshClick);
        HookRightClickAsLeft(UpdateMenuItem, UpdateClick);
        HookRightClickAsLeftToggle(AutoStartItem, AutoStartClick);
        HookRightClickAsLeft(AboutMenuItem, AboutClick);
        HookRightClickAsLeft(ExitMenuItem, ExitClick);
    }

    void HookRightClickAsLeft(MenuFlyoutItem item, RoutedEventHandler click)
    {
        item.RightTapped += (s, e) =>
        {
            e.Handled = true;
            try { click(item, new RoutedEventArgs()); }
            catch (Exception ex) { App.LogStatic("RightTapped click ex: " + ex.Message); }
            try { (Tray.ContextFlyout as MenuFlyout)?.Hide(); } catch { }
        };
    }

    void HookRightClickAsLeftToggle(ToggleMenuFlyoutItem item, RoutedEventHandler click)
    {
        item.RightTapped += (s, e) =>
        {
            e.Handled = true;
            item.IsChecked = !item.IsChecked;   // ToggleMenuFlyoutItem обычно сам toggle'ится по Click, вручную повторяем
            try { click(item, new RoutedEventArgs()); }
            catch (Exception ex) { App.LogStatic("RightTapped toggle ex: " + ex.Message); }
            try { (Tray.ContextFlyout as MenuFlyout)?.Hide(); } catch { }
        };
    }

    void OpenClick(object sender, RoutedEventArgs e) => OnOpenFromMenu?.Invoke();
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

    /// <summary>Показать progress toast для download и вернуть IProgress который его обновляет.
    /// Toast имеет Tag = "monitune-update-progress" — последующие Show с тем же Tag заменяют предыдущий,
    /// так что update идёт in-place, без спама уведомлений.</summary>
    public IProgress<double> ShowDownloadProgress(string version)
    {
        const string tag = "monitune-update-progress";
        var progressData = new Microsoft.Windows.AppNotifications.AppNotificationProgressData(1)
        {
            Title = $"Загрузка MoniTune {version}",
            Value = 0,
            ValueStringOverride = "0%",
            Status = "Скачиваю обновление…",
        };
        try
        {
            var builder = new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder()
                .AddText($"Обновление MoniTune {version}")
                .AddProgressBar(new Microsoft.Windows.AppNotifications.Builder.AppNotificationProgressBar()
                    .BindTitle().BindValueStringOverride().BindStatus());
            var notification = builder.BuildNotification();
            notification.Tag = tag;
            notification.Progress = progressData;
            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex) { App.LogStatic("ShowDownloadProgress ex: " + ex.Message); }

        int lastPercent = -1;
        return new Progress<double>(frac =>
        {
            int p = (int)(frac * 100);
            if (p == lastPercent) return;
            lastPercent = p;
            var data = new Microsoft.Windows.AppNotifications.AppNotificationProgressData(2)
            {
                Title = $"Загрузка MoniTune {version}",
                Value = frac,
                ValueStringOverride = p + "%",
                Status = p < 100 ? "Скачиваю обновление…" : "Устанавливаю…",
            };
            try
            {
                _ = Microsoft.Windows.AppNotifications.AppNotificationManager.Default.UpdateAsync(data, tag);
            }
            catch (Exception ex) { App.LogStatic("progress update ex: " + ex.Message); }
        });
    }

    async void UpdateClick(object sender, RoutedEventArgs e)
    {
        var info = _pendingUpdate;
        if (info == null) return;
        try
        {
            App.LogStatic($"User clicked update → {info.Version}");
            var progress = ShowDownloadProgress(info.Version);
            bool ok = await UpdateService.DownloadAndInstallAsync(info, progress);
            if (ok) App.LogStatic("Update installed — приложение должно перезапуститься");
            else ShowError($"Не удалось установить обновление {info.Version}. Проверьте соединение и лог.");
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
