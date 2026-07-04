using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MonitorTune;

// Собирает crash-репорт при UnhandledException. Пишет локально в crashes/crash-{ts}.json.
// Если Settings.TelemetryEnabled + указан endpoint — отправляет POST'ом.
// Всё в try/catch — crash reporter не должен сам крашить приложение.
public static class CrashReporter
{
    static string CrashDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "crashes");

    public static void Install()
    {
        try
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                try { WriteCrash("AppDomain", e.ExceptionObject as Exception, e.IsTerminating); } catch { }
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                try { WriteCrash("UnobservedTask", e.Exception, false); e.SetObserved(); } catch { }
            };
            try
            {
                Microsoft.UI.Xaml.Application.Current.UnhandledException += (_, e) =>
                {
                    try { WriteCrash("XAML", e.Exception, false); }
                    catch { }
                };
            }
            catch { }
            Directory.CreateDirectory(CrashDir);
            App.LogStatic("CrashReporter installed → " + CrashDir);
        }
        catch (Exception ex) { App.LogStatic("CrashReporter install ex: " + ex); }
    }

    static void WriteCrash(string source, Exception? ex, bool terminating)
    {
        try
        {
            Directory.CreateDirectory(CrashDir);
            var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            var path = Path.Combine(CrashDir, $"crash-{ts}-{pid}.json");

            var report = new
            {
                timestamp = DateTimeOffset.Now.ToString("o"),
                source,
                terminating,
                appVersion = TryGetAppVersion(),
                osVersion = Environment.OSVersion.VersionString,
                clr = Environment.Version.ToString(),
                culture = System.Globalization.CultureInfo.CurrentCulture.Name,
                exceptionType = ex?.GetType().FullName,
                message = ex?.Message,
                stack = ex?.ToString(),
                logTail = App.LogTailSnapshot(),
            };
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            App.LogStatic($"CrashReport written: {path}");

            // Отправка на endpoint (fire-and-forget), только если opt-in + endpoint настроен.
            try
            {
                if (SettingsStore.Current.TelemetryEnabled &&
                    !string.IsNullOrWhiteSpace(SettingsStore.Current.TelemetryEndpoint))
                {
                    _ = SendAsync(SettingsStore.Current.TelemetryEndpoint!, json);
                }
            }
            catch { }
        }
        catch { }
    }

    static async Task SendAsync(string url, string body)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            await http.PostAsync(url, content);
        }
        catch (Exception ex) { App.LogStatic("Telemetry send ex: " + ex.Message); }
    }

    static string TryGetAppVersion()
    {
        try
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
        catch { return "unknown"; }
    }
}
