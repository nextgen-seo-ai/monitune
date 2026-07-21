using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Management.Deployment;

namespace MonitorTune;

// Auto-update поток:
//   1. Пробуем primary: GitHub Releases latest.json (публичный, TLS+CDN+immutable tags).
//   2. Если недоступен → приватные mesh-fallback URL'ы (зашиты compile-time в PrivateFallbackManifests).
//   3. Скачиваем latest.json + latest.json.sig, verify Ed25519 подпись — только если ok идём дальше.
//   4. Sanity-checks: version parse, downgrade protection.
//   5. Скачиваем MSIX, sha256 verify.
//   6. Authenticode verify через WinVerifyTrust + thumbprint check.
//   7. PackageManager.AddPackageAsync (Windows ещё раз проверит подпись).
public static class UpdateService
{
    public static event Action<UpdateInfo>? UpdateAvailable;

    // ── Публичные компоненты ─────────────────────────────────────────
    public class UpdateInfo
    {
        public string Version { get; set; } = "";
        public string MsixUrl { get; set; } = "";
        public string? SetupUrl { get; set; }
        public string? Sha256 { get; set; }
        public string? MinVersion { get; set; }
        public string? Notes { get; set; }
        public bool Mandatory { get; set; }
    }

    // ── Manifest DTO ─────────────────────────────────────────────────
    class Manifest
    {
        public string version { get; set; } = "";
        public string msix { get; set; } = "";
        public string? setup { get; set; }
        public string? sha256 { get; set; }
        public string? min_version { get; set; }
        public string? notes { get; set; }
        public bool mandatory { get; set; }
        public string? released { get; set; }
    }

    // ── Compile-time константы ───────────────────────────────────────

    /// <summary>Primary source: GitHub Releases latest.json (публичный, авторитетный, immutable).</summary>
    const string GitHubManifestUrl = "https://github.com/nextgen-seo-ai/monitune/releases/latest/download/latest.json";

    /// <summary>Приватные mesh-fallback URL'ы. Compile-time const — НЕ хранятся в settings.json,
    /// НЕ упоминаются в landing/README, НЕ логируются полностью (только hash).
    /// Используются только если все попытки GitHub провалились.</summary>
    static readonly string[] PrivateFallbackManifests = Array.Empty<string>();
    // TODO: заменить Array.Empty на реальные приватные mesh-URL'ы владельца когда будут:
    // static readonly string[] PrivateFallbackManifests = new[]
    // {
    //     "https://mesh1.internal.example/monitune/latest.json",
    //     "https://mesh2.internal.example/monitune/latest.json",
    //     "https://mesh3.internal.example/monitune/latest.json",
    //     "https://mesh4.internal.example/monitune/latest.json",
    // };

    /// <summary>Ed25519 публичный ключ для верификации подписи manifest'а (32 байта, base64).
    /// Приватная пара — только в GitHub Actions Secret MANIFEST_ED25519_PRIVATE.
    /// Как обновить — см. release/GENERATE-KEYS.md.</summary>
    const string ManifestEd25519PublicKeyBase64 = "066r9qodOaq0c8NkC4eh2Dab3h5PutSCQxZjWDb3FSA=";

    /// <summary>Разрешённые thumbprints издателя MSIX (SHA-1 подписи).
    /// При ротации cert — добавляется новый thumbprint, старый оставляется на dual-sign период.</summary>
    static readonly string[] AllowedSignerThumbprints = new[]
    {
        "31D6929559D15ACD3AC47D4E28A7C8DC3CF405B8",
    };

    // ── Точки входа ──────────────────────────────────────────────────

    /// <summary>Проверка обновлений в фоне (для вызова из App.OnLaunched).</summary>
    public static void CheckInBackground()
    {
        _ = Task.Run(async () =>
        {
            try { await CheckAsync(); }
            catch (Exception ex) { App.LogStatic("UpdateService.CheckAsync ex: " + ex.Message); }
        });
    }

    public static async Task<UpdateInfo?> CheckAsync(bool fireEvent = true)
    {
        // Порядок источников: сначала GitHub, потом compile-time mesh fallback.
        var sources = new List<string> { GitHubManifestUrl };
        sources.AddRange(PrivateFallbackManifests);
        // Пользовательский override из settings (обычно пусто) — идёт LAST, только для локального testing.
        var overrides = SettingsStore.Current.UpdateManifestUrls;
        if (overrides != null) sources.AddRange(overrides);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MoniTune/" + CurrentVersion() + " (WinUI3)");
        // GitHub CDN Fastly кэширует /releases/latest/download/* редиректы на 5-10 минут.
        // Cache-bust query + Cache-Control гарантируют свежий ответ на каждой проверке.
        http.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
        http.DefaultRequestHeaders.Add("Pragma", "no-cache");

        byte[]? manifestBytes = null;
        byte[]? sigBytes = null;
        string? sourceUrl = null;

        // Уникальный per-check timestamp — CDN не может смэтчить с закэшированным URL.
        string cacheBust = "_=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var url in sources)
        {
            try
            {
                string urlWithBust = url + (url.Contains('?') ? "&" : "?") + cacheBust;
                App.LogStatic($"UpdateService: probing {RedactUrl(url)}");
                var manifestResp = await http.GetAsync(urlWithBust, HttpCompletionOption.ResponseContentRead);
                if (!manifestResp.IsSuccessStatusCode)
                {
                    App.LogStatic($"UpdateService: {RedactUrl(url)} → HTTP {(int)manifestResp.StatusCode}");
                    continue;
                }
                var mBytes = await manifestResp.Content.ReadAsByteArrayAsync();

                var sigUrl = url + ".sig";
                string sigUrlWithBust = sigUrl + (sigUrl.Contains('?') ? "&" : "?") + cacheBust;
                var sigResp = await http.GetAsync(sigUrlWithBust, HttpCompletionOption.ResponseContentRead);
                if (!sigResp.IsSuccessStatusCode)
                {
                    App.LogStatic($"UpdateService: {RedactUrl(sigUrl)} → HTTP {(int)sigResp.StatusCode} (нет .sig, пропуск)");
                    continue;
                }
                var sBytes = await sigResp.Content.ReadAsByteArrayAsync();

                manifestBytes = mBytes;
                sigBytes = sBytes;
                sourceUrl = url;
                break;
            }
            catch (Exception ex)
            {
                App.LogStatic($"UpdateService: {RedactUrl(url)} ex: {ex.Message}");
            }
        }

        if (manifestBytes == null || sigBytes == null)
        {
            App.LogStatic("UpdateService: все источники недоступны или без .sig");
            return null;
        }

        // Ed25519 verify до deserialization.
        if (!ManifestVerifier.Verify(manifestBytes, sigBytes))
        {
            App.LogStatic($"UpdateService: SIGNATURE INVALID для {RedactUrl(sourceUrl!)} — abort");
            return null;
        }
        App.LogStatic($"UpdateService: manifest подпись Ed25519 верифицирована ({RedactUrl(sourceUrl!)})");

        // Parse.
        Manifest? manifest;
        try
        {
            var json = Encoding.UTF8.GetString(manifestBytes);
            manifest = JsonSerializer.Deserialize<Manifest>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            App.LogStatic($"UpdateService: manifest JSON parse ex: {ex.Message}");
            return null;
        }

        if (manifest == null || string.IsNullOrWhiteSpace(manifest.version) || string.IsNullOrWhiteSpace(manifest.msix))
        {
            App.LogStatic("UpdateService: manifest пустой или без required полей");
            return null;
        }

        // Downgrade protection.
        var current = CurrentVersion();
        if (!IsNewer(manifest.version, current))
        {
            bool allow = SettingsStore.Current.AllowDowngrade;
            if (!allow)
            {
                App.LogStatic($"UpdateService: manifest {manifest.version} <= current {current} — downgrade заблокирован (AllowDowngrade=false)");
                return null;
            }
            App.LogStatic($"UpdateService: downgrade разрешён (manifest {manifest.version} <= current {current})");
        }

        var info = new UpdateInfo
        {
            Version = manifest.version,
            MsixUrl = manifest.msix,
            SetupUrl = manifest.setup,
            Sha256 = manifest.sha256,
            MinVersion = manifest.min_version,
            Notes = manifest.notes,
            Mandatory = manifest.mandatory,
        };
        App.LogStatic($"UpdateService: доступно обновление {info.Version}");
        // fireEvent=false когда вызвано из InstallPendingUpdate (toast click) —
        // иначе UpdateAvailable → ShowUpdateAvailable → второй toast во время установки.
        if (fireEvent)
        {
            try { UpdateAvailable?.Invoke(info); } catch (Exception ex) { App.LogStatic("UpdateAvailable handler ex: " + ex); }
        }
        return info;
    }

    public static async Task<bool> DownloadAndInstallAsync(UpdateInfo info, IProgress<double>? progress = null)
    {
        try
        {
            // ВАЖНО: PackageManager.AddPackageAsync работает как система (не как MSIX-приложение)
            // и НЕ применяет FS virtualization sandbox'а. Если сохранить через
            // Environment.SpecialFolder.LocalApplicationData → .NET спрячет файл в
            // Packages\<PFN>\LocalCache\Local\, а deployment service будет искать по
            // виртуальному C:\Users\<u>\AppData\Local\updates\ → 0x80073CF0 + 0x80070003.
            // Правильно — физический sandbox path через ApplicationData.Current.LocalCacheFolder.Path,
            // deployment API умеет его читать через package identity.
            string localCache;
            try
            {
                localCache = Path.Combine(Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path, "updates");
            }
            catch (InvalidOperationException)
            {
                // Fallback для unpackaged сборки (не MSIX — например dotnet run локально).
                localCache = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "updates");
            }
            Directory.CreateDirectory(localCache);
            var msixPath = Path.Combine(localCache, $"MonitorTune-{info.Version}.msix");
            App.LogStatic($"UpdateService: MSIX path → {msixPath}");

            App.LogStatic($"UpdateService: скачиваю {RedactUrl(info.MsixUrl)}");
            // Timeout = InfiniteTimeSpan т.к. self-contained MSIX ~86 MB,
            // на 1 Mbps качается ~12 мин — HttpClient.Timeout охватывает весь lifetime
            // включая streaming, 10-мин потолок обрывал download у юзеров с медленным
            // интернетом. Idle-timeout на уровне stream reads даёт лучший контроль.
            using (var http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan })
            using (var resp = await http.GetAsync(info.MsixUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                if (!resp.IsSuccessStatusCode)
                {
                    App.LogStatic($"UpdateService: download HTTP {(int)resp.StatusCode}");
                    return false;
                }
                long total = resp.Content.Headers.ContentLength ?? -1;
                App.LogStatic($"UpdateService: total size {(total > 0 ? total / 1024 / 1024 + " MB" : "unknown")}");
                using var fs = new FileStream(msixPath, FileMode.Create, FileAccess.Write, FileShare.None);
                using var stream = await resp.Content.ReadAsStreamAsync();
                var buf = new byte[64 * 1024];
                long read = 0;
                int n;
                int lastLoggedPercent = -1;
                var lastReadAt = Environment.TickCount;
                while ((n = await stream.ReadAsync(buf)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, n));
                    read += n;
                    lastReadAt = Environment.TickCount;
                    if (total > 0)
                    {
                        double frac = (double)read / total;
                        progress?.Report(frac);
                        int percent = (int)(frac * 100);
                        // Лог каждые 10% — иначе спам при 86 MB.
                        if (percent / 10 != lastLoggedPercent / 10)
                        {
                            App.LogStatic($"UpdateService: download {percent}% ({read / 1024 / 1024} MB)");
                            lastLoggedPercent = percent;
                        }
                    }
                }
                App.LogStatic($"UpdateService: download done ({read / 1024 / 1024} MB)");
            }

            // 1. SHA-256 check.
            if (!string.IsNullOrWhiteSpace(info.Sha256))
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                using var fs = File.OpenRead(msixPath);
                var hash = Convert.ToHexString(sha.ComputeHash(fs));
                if (!string.Equals(hash, info.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    App.LogStatic($"UpdateService: SHA256 MISMATCH (got {hash}, expected {info.Sha256}) — abort + delete");
                    try { File.Delete(msixPath); } catch { }
                    return false;
                }
                App.LogStatic("UpdateService: SHA-256 ok");
            }
            else
            {
                App.LogStatic("UpdateService: manifest без sha256 — пропускаю hash-check (не best practice)");
            }

            // 2. Authenticode verify через WinVerifyTrust + thumbprint check.
            var (authOk, signerThumbprint) = AuthenticodeVerifier.VerifyMsix(msixPath);
            if (!authOk)
            {
                App.LogStatic($"UpdateService: WinVerifyTrust FAILED (thumbprint={signerThumbprint ?? "?"}) — abort");
                try { File.Delete(msixPath); } catch { }
                return false;
            }
            bool thumbOk = false;
            foreach (var allowed in AllowedSignerThumbprints)
            {
                if (string.Equals(allowed, signerThumbprint, StringComparison.OrdinalIgnoreCase)) { thumbOk = true; break; }
            }
            if (!thumbOk)
            {
                App.LogStatic($"UpdateService: signer thumbprint {signerThumbprint} НЕ В whitelist — abort");
                try { File.Delete(msixPath); } catch { }
                return false;
            }
            App.LogStatic($"UpdateService: Authenticode ok, thumbprint {signerThumbprint} доверенный");

            // 3. Install.
            App.LogStatic($"UpdateService: installing {msixPath}");

            // (a) Zero-cost fallback: RegisterApplicationRestart. Для packaged MSIX
            // не гарантирован (WindowsApps path меняется при install), но иногда срабатывает.
            try
            {
                int rar = Native.RegisterApplicationRestart(null, 0);
                App.LogStatic($"UpdateService: RegisterApplicationRestart hr=0x{rar:X8} ({(rar == 0 ? "OK" : "FAIL")})");
            }
            catch (Exception ex) { App.LogStatic($"RegisterApplicationRestart ex: {ex.Message}"); }

            // (b) Основной механизм: одноразовая Task Scheduler задача.
            // Планируем ДО AddPackageAsync (после kill наш процесс мёртв).
            // Task Scheduler service живёт в отдельном job'е, переживёт наш kill.
            // Delay 90 сек покрывает worst-case: slow disk, Defender full-scan, staging.
            // AUMID резолвится динамически из Package.Current.GetAppListEntries().
            const int RestartDelaySeconds = 90;
            bool scheduled = ScheduleOneShotRestart(RestartDelaySeconds, out string? scheduleError);
            if (!scheduled)
                App.LogStatic($"UpdateService: schtasks scheduling FAILED ({scheduleError ?? "?"}) — user will need to launch manually");

            var pm = new PackageManager();
            var op = pm.AddPackageAsync(new Uri(msixPath), null,
                DeploymentOptions.ForceApplicationShutdown | DeploymentOptions.ForceTargetApplicationShutdown);
            var result = await op;
            if (result.ExtendedErrorCode != null)
            {
                App.LogStatic($"UpdateService: install error {result.ExtendedErrorCode.HResult:X}: {result.ErrorText}");
                return false;
            }
            App.LogStatic($"UpdateService: install OK — task will restart in ~{RestartDelaySeconds}s");
            return true;
        }
        catch (Exception ex)
        {
            App.LogStatic("UpdateService.DownloadAndInstallAsync ex: " + ex);
            return false;
        }
    }

    // ── Утилиты ──────────────────────────────────────────────────────

    internal static string ManifestPublicKeyForVerify => ManifestEd25519PublicKeyBase64;

    static string CurrentVersion()
    {
        try
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
        catch { return "0.0.0.0"; }
    }

    static bool IsNewer(string manifestVer, string currentVer)
    {
        try
        {
            var a = Version.Parse(NormalizeVersion(manifestVer));
            var b = Version.Parse(NormalizeVersion(currentVer));
            return a > b;
        }
        catch { return false; }
    }

    static string NormalizeVersion(string s)
    {
        var parts = s.Split('.');
        var list = new List<string>(parts);
        while (list.Count < 4) list.Add("0");
        return string.Join(".", list);
    }

    /// <summary>Обфускация URL для лога: сохраняем схему и путь, host заменяем на hash.
    /// Приватные mesh-хосты не должны утекать через crash-report telemetry.</summary>
    static string RedactUrl(string url)
    {
        try
        {
            var u = new Uri(url);
            // Public GitHub — не редактируем (уже публичный).
            if (u.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase) ||
                u.Host.EndsWith("githubusercontent.com", StringComparison.OrdinalIgnoreCase))
                return url;
            // Приватный host → hash.
            var hostHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(u.Host))).Substring(0, 8);
            return $"{u.Scheme}://[private:{hostHash}]{u.AbsolutePath}";
        }
        catch { return "[unparseable-url]"; }
    }

    // ── Post-update auto-restart (scheduled-task-oneshot approach) ──────

    // Плоское имя scheduled task — без folder, чтобы не требовать прав на root namespace.
    const string RestartTaskName = "MonitorTunePostUpdateRestart";

    // Marker-файл в НАСТОЯЩЕМ %LOCALAPPDATA%\MonitorTune\pending-restart.txt
    // (Environment.SpecialFolder.LocalApplicationData из runFullTrust MSIX процесса
    // возвращает физический путь, не MSIX-виртуализированный LocalCacheFolder).
    // Marker виден и старой и новой сессии → идемпотентный cleanup.
    static string PendingRestartMarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MonitorTune", "pending-restart.txt");

    /// <summary>Планирует одноразовый рестарт через N секунд через Task Scheduler.
    /// AUMID резолвится ДИНАМИЧЕСКИ из Package.Current.GetAppListEntries() —
    /// никакого hardcode "!App". Возвращает true если schtasks /Create отработал c rc=0.</summary>
    static bool ScheduleOneShotRestart(int delaySeconds, out string? error)
    {
        error = null;
        try
        {
            // 1. Динамический AUMID.
            string? aumid = null;
            try
            {
                var entries = Windows.ApplicationModel.Package.Current.GetAppListEntries();
                if (entries != null && entries.Count > 0)
                    aumid = entries[0].AppUserModelId;
            }
            catch (Exception ex) { App.LogStatic($"GetAppListEntries ex: {ex.Message}"); }

            if (string.IsNullOrWhiteSpace(aumid))
            {
                var pfn = Windows.ApplicationModel.Package.Current.Id.FamilyName;
                aumid = $"{pfn}!App";
                App.LogStatic($"UpdateService: AUMID fallback → {aumid}");
            }
            else
            {
                App.LogStatic($"UpdateService: AUMID from GetAppListEntries → {aumid}");
            }

            // 2. Marker-файл ДО регистрации task. При следующем запуске (любом)
            // ResumeAfterUpdateIfNeeded() почистит task и marker.
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PendingRestartMarkerPath)!);
                var lines = new[]
                {
                    $"aumid={aumid}",
                    $"task={RestartTaskName}",
                    $"scheduled_utc={DateTime.UtcNow:o}",
                    $"trigger_utc={DateTime.UtcNow.AddSeconds(delaySeconds):o}",
                };
                File.WriteAllLines(PendingRestartMarkerPath, lines, Encoding.UTF8);
            }
            catch (Exception ex) { App.LogStatic($"UpdateService: marker write ex: {ex.Message}"); }

            // 3. schtasks /XML — инвариантный формат даты, EndBoundary + DeleteExpiredTaskAfter
            // → задача исчезает сама через 10 мин после окна старта. Окно старта 30 мин
            // покрывает sleep/hibernate. AllowStartOnDemand=true разрешает ручной retry.
            var triggerLocal = DateTime.Now.AddSeconds(delaySeconds);
            var endBoundary = triggerLocal.AddMinutes(30);
            string startBoundary = triggerLocal.ToString("yyyy-MM-ddTHH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture);
            string endBoundaryStr = endBoundary.ToString("yyyy-MM-ddTHH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture);

            string xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>MoniTune one-shot post-update restart</Description>
    <URI>\{RestartTaskName}</URI>
  </RegistrationInfo>
  <Triggers>
    <TimeTrigger>
      <StartBoundary>{startBoundary}</StartBoundary>
      <EndBoundary>{endBoundaryStr}</EndBoundary>
      <Enabled>true</Enabled>
    </TimeTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
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
    <ExecutionTimeLimit>PT2M</ExecutionTimeLimit>
    <Priority>7</Priority>
    <DeleteExpiredTaskAfter>PT10M</DeleteExpiredTaskAfter>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>explorer.exe</Command>
      <Arguments>shell:AppsFolder\{aumid}</Arguments>
    </Exec>
  </Actions>
</Task>";

            // Task Scheduler XML требует UTF-16 LE с BOM.
            string xmlPath = Path.Combine(Path.GetTempPath(), $"MonitorTuneRestartTask-{Guid.NewGuid():N}.xml");
            File.WriteAllText(xmlPath, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Create /F /TN \"{RestartTaskName}\" /XML \"{xmlPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) { error = "Process.Start returned null"; return false; }
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                bool exited = p.WaitForExit(10000);
                if (!exited)
                {
                    try { p.Kill(); } catch { }
                    error = "schtasks timeout";
                    App.LogStatic($"UpdateService: schtasks TIMEOUT");
                    return false;
                }
                App.LogStatic($"UpdateService: schtasks rc={p.ExitCode} out={stdout.Trim()} err={stderr.Trim()}");
                if (p.ExitCode != 0)
                {
                    error = $"schtasks rc={p.ExitCode}: {stderr.Trim()}";
                    return false;
                }
                return true;
            }
            finally
            {
                try { File.Delete(xmlPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            App.LogStatic($"UpdateService.ScheduleOneShotRestart ex: {ex}");
            return false;
        }
    }

    /// <summary>Вызывать в самом начале App.OnLaunched после инициализации логгера.
    /// Cleanup scheduled task + marker: (а) когда новый процесс поднялся через task,
    /// (б) когда юзер вручную запустил приложение раньше task — предотвращает дубликат.</summary>
    public static void ResumeAfterUpdateIfNeeded()
    {
        try
        {
            string marker = PendingRestartMarkerPath;
            if (!File.Exists(marker)) return;

            App.LogStatic("UpdateService: pending-restart marker found — cleaning up scheduled task");

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Delete /F /TN \"{RestartTaskName}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p != null)
                {
                    p.WaitForExit(5000);
                    App.LogStatic($"UpdateService: cleanup schtasks /Delete rc={p.ExitCode}");
                }
            }
            catch (Exception ex) { App.LogStatic($"UpdateService: schtasks /Delete ex: {ex.Message}"); }

            try { File.Delete(marker); }
            catch (Exception ex) { App.LogStatic($"UpdateService: marker delete ex: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            App.LogStatic("UpdateService.ResumeAfterUpdateIfNeeded ex: " + ex);
        }
    }
}
