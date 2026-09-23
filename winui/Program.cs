using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace MonitorTune;

// Кастомный Main с single-instance логикой — заменяет автогенерируемый Main из App.g.i.cs
// (отключён через <DefineConstants>DISABLE_XAML_GENERATED_MAIN в csproj).
//
// Зачем: без single-instance каждый клик по toast поднимает НОВЫЙ instance приложения
// (второй tray-иконка!), а его activation args теряются, потому что WinUI 3 OnLaunched
// не получает исходные ToastNotificationActivatedEventArgs.
public static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // --autostart передаёт задание планировщика (AutostartService). Если приложение уже
        // поднято через StartupTask, второй копии делать нечего: выходим молча, БЕЗ
        // RedirectActivation — иначе главный instance примет это за клик по ярлыку и
        // покажет панель поверх рабочего стола сразу после входа в систему.
        bool fromAutostartTask = Array.Exists(args,
            a => string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase));

        WinRT.ComWrappersSupport.InitializeComWrappers();

        var mainInstance = AppInstance.FindOrRegisterForKey("MonitorTune-Main");
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();

        if (!mainInstance.IsCurrent)
        {
            if (fromAutostartTask) return 0;
            // Основной instance уже запущен — пересылаем ему activation (toast click и т.д.)
            // и выходим. Так у пользователя всегда один tray-иконка.
            // GetResult() вместо .Wait() чтобы не оборачивать exception в AggregateException,
            // и потому что нет проблемы deadlock: у нас нет SynchronizationContext на этом
            // этапе (он ставится только внутри Application.Start в main instance).
            mainInstance.RedirectActivationToAsync(activation).AsTask().GetAwaiter().GetResult();
            return 0;
        }

        // Мы — единственный instance. Подпишемся на будущие activation
        // (toast click, protocol handler и т.д.) — они приходят через это событие.
        mainInstance.Activated += (_, args) => App.HandleRedirectedActivation(args);

        // Язык ставим до Application.Start: XAML с x:Uid подставляет строки в момент
        // загрузки разметки, поэтому переключение после создания окон уже не подействует.
        App.LaunchedByAutostartTask = fromAutostartTask;
        SettingsStore.Load();
        Loc.ApplyLanguage(SettingsStore.Current.Language);

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }
}
