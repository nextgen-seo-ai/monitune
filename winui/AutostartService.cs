using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace MonitorTune;

/// <summary>Запуск вместе с Windows.
///
/// Основной путь — StartupTask из манифеста: Windows сама поднимает приложение при входе.
/// Но после перезагрузки, которую устраивает Windows Update, он срабатывает не всегда:
/// 16.09.2026 на тестовой машине после такой перезагрузки (TrustedInstaller, вход в 03:38)
/// приложение не стартовало, хотя программы из обычного списка автозагрузки поднялись.
/// Поэтому дублируем запуск заданием планировщика с триггером «при входе в систему» —
/// оно регистрируется без прав администратора (проверено: schtasks /Create rc=0 из
/// непривилегированного процесса).
///
/// Две копии друг другу не мешают: задание запускает MoniTune.exe --autostart, и вторая
/// копия выходит молча, не пересылая activation главной (см. Program.Main). Иначе панель
/// выскакивала бы поверх рабочего стола сразу после входа.</summary>
public static class AutostartService
{
    /// <summary>Имя задания в планировщике. У тестовой сборки Identity другой, и имя тоже —
    /// иначе она перезапишет задание установленной копии.</summary>
    public static string TaskName
    {
        get
        {
            try
            {
                string id = Package.Current.Id.Name;
                return id == "MonitorTune" ? "MoniTune Autostart" : $"MoniTune Autostart ({id})";
            }
            catch { return "MoniTune Autostart"; }
        }
    }

    const string StartupTaskId = "MonitorTuneStartup";

    /// <summary>Псевдоним из манифеста (windows.appExecutionAlias). Задание планировщика
    /// работает вне пакета, поэтому запускает приложение через этот exe-переходник.</summary>
    static string AliasPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "WindowsApps", "MoniTune.exe");

    static void L(string s) => App.LogStatic("Autostart: " + s);

    public static StartupTaskState GetState()
    {
        try
        {
            return StartupTask.GetAsync(StartupTaskId).AsTask().GetAwaiter().GetResult().State;
        }
        catch (Exception ex)
        {
            L("GetState ex: " + ex.Message);
            return StartupTaskState.Disabled;
        }
    }

    public static bool IsOn(StartupTaskState s) =>
        s == StartupTaskState.Enabled || s == StartupTaskState.EnabledByPolicy;

    /// <summary>Вызывается на старте приложения. Делает три вещи: разово включает автозапуск
    /// у тех, кто обновился со старой версии (там StartupTask в манифесте был Enabled="false",
    /// и без ручной галочки приложение с Windows не стартовало), приводит задание планировщика
    /// в соответствие с состоянием автозапуска и снимает задание, если автозапуск выключили
    /// в параметрах Windows.</summary>
    public static async Task ApplyAsync(bool launchedByTask)
    {
        var state = GetState();
        L($"состояние={state}, запущено заданием={launchedByTask}");

        // DisabledByUser — выключено в параметрах Windows, DisabledByPolicy — запрещено
        // политикой. Программно это не обойти, да и не нужно: выбор пользователя важнее.
        if (state == StartupTaskState.DisabledByUser || state == StartupTaskState.DisabledByPolicy)
        {
            L("автозапуск выключен в Windows — снимаю задание");
            Sync(false);
            return;
        }

        if (!SettingsStore.Current.AutostartDefaultApplied)
        {
            if (state == StartupTaskState.Disabled)
            {
                try
                {
                    var task = await StartupTask.GetAsync(StartupTaskId);
                    state = await task.RequestEnableAsync();
                    L($"разовое включение при обновлении → {state}");
                }
                catch (Exception ex) { L("RequestEnable ex: " + ex.Message); }
            }

            // Флаг ставим, только если автозапуск теперь включён. Иначе разовая попытка
            // сгорит на временной ошибке и больше никогда не повторится.
            if (IsOn(state))
            {
                SettingsStore.Current.AutostartDefaultApplied = true;
                try { SettingsStore.Save(); } catch (Exception ex) { L("сохранение настроек ex: " + ex.Message); }
            }
        }

        Sync(IsOn(state));
    }

    /// <summary>Создаёт или снимает задание планировщика под текущее состояние автозапуска.</summary>
    public static void Sync(bool wanted)
    {
        try
        {
            if (wanted) CreateTask();
            else RemoveTask();
        }
        catch (Exception ex) { L("Sync ex: " + ex.Message); }
    }

    static void CreateTask()
    {
        string exe = AliasPath;
        if (!File.Exists(exe))
        {
            // Псевдоним появляется вместе с установкой пакета. Нет его — не из чего делать
            // задание; основной автозапуск через StartupTask при этом работает.
            L($"псевдонима нет ({exe}) — задание не создаю");
            return;
        }

        string account = $"{Environment.UserDomainName}\\{Environment.UserName}";
        string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? account;

        // Задержка 90 секунд: на обычном входе StartupTask поднимает приложение примерно
        // через минуту, и запуск по заданию тогда просто выходит молча. Задание нужно для
        // случая, когда StartupTask не сработал вовсе.
        string xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>MoniTune: запуск при входе в систему</Description>
    <URI>\{TaskName}</URI>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{account}</UserId>
      <Delay>PT90S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{sid}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{exe}</Command>
      <Arguments>--autostart</Arguments>
    </Exec>
  </Actions>
</Task>";

        // Task Scheduler читает XML только в UTF-16 LE с BOM. Имя файла постоянное:
        // перезаписываем один и тот же файл, чтобы не плодить мусор во временной папке.
        string xmlPath = Path.Combine(Path.GetTempPath(), "MoniTuneAutostart.xml");
        File.WriteAllText(xmlPath, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

        // /F — перезаписать: при каждом старте задание приводится к актуальному виду
        // (путь мог измениться, старая версия могла записать другое действие).
        int rc = RunSchtasks($"/Create /F /TN \"{TaskName}\" /XML \"{xmlPath}\"");
        L(rc == 0 ? $"задание создано: {exe} --autostart" : $"создать задание не удалось, rc={rc}");
    }

    static void RemoveTask()
    {
        int rc = RunSchtasks($"/Delete /F /TN \"{TaskName}\"");
        // rc=1 — задания нет, это нормальное состояние после выключения автозапуска.
        L(rc == 0 ? "задание снято" : $"задания нет или снять не удалось, rc={rc}");
    }

    static int RunSchtasks(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi);
        if (p == null) { L("schtasks не запустился"); return -1; }

        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(15000))
        {
            try { p.Kill(); } catch { }
            L("schtasks завис");
            return -2;
        }
        if (p.ExitCode != 0) L($"schtasks rc={p.ExitCode} out={stdout.Trim()} err={stderr.Trim()}");
        return p.ExitCode;
    }
}
