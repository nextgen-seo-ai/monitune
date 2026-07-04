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

    public static async Task<UpdateInfo?> CheckAsync()
    {
        // Порядок источников: сначала GitHub, потом compile-time mesh fallback.
        var sources = new List<string> { GitHubManifestUrl };
        sources.AddRange(PrivateFallbackManifests);
        // Пользовательский override из settings (обычно пусто) — идёт LAST, только для локального testing.
        var overrides = SettingsStore.Current.UpdateManifestUrls;
        if (overrides != null) sources.AddRange(overrides);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MonitorTune/" + CurrentVersion() + " (WinUI3)");

        byte[]? manifestBytes = null;
        byte[]? sigBytes = null;
        string? sourceUrl = null;

        foreach (var url in sources)
        {
            try
            {
                App.LogStatic($"UpdateService: probing {RedactUrl(url)}");
                var manifestResp = await http.GetAsync(url, HttpCompletionOption.ResponseContentRead);
                if (!manifestResp.IsSuccessStatusCode)
                {
                    App.LogStatic($"UpdateService: {RedactUrl(url)} → HTTP {(int)manifestResp.StatusCode}");
                    continue;
                }
                var mBytes = await manifestResp.Content.ReadAsByteArrayAsync();

                var sigUrl = url + ".sig";
                var sigResp = await http.GetAsync(sigUrl, HttpCompletionOption.ResponseContentRead);
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
        try { UpdateAvailable?.Invoke(info); } catch (Exception ex) { App.LogStatic("UpdateAvailable handler ex: " + ex); }
        return info;
    }

    public static async Task<bool> DownloadAndInstallAsync(UpdateInfo info, IProgress<double>? progress = null)
    {
        try
        {
            var localCache = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "updates");
            Directory.CreateDirectory(localCache);
            var msixPath = Path.Combine(localCache, $"MonitorTune-{info.Version}.msix");

            App.LogStatic($"UpdateService: скачиваю {RedactUrl(info.MsixUrl)}");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            using (var resp = await http.GetAsync(info.MsixUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                if (!resp.IsSuccessStatusCode)
                {
                    App.LogStatic($"UpdateService: download HTTP {(int)resp.StatusCode}");
                    return false;
                }
                long total = resp.Content.Headers.ContentLength ?? -1;
                using var fs = new FileStream(msixPath, FileMode.Create, FileAccess.Write, FileShare.None);
                using var stream = await resp.Content.ReadAsStreamAsync();
                var buf = new byte[64 * 1024];
                long read = 0;
                int n;
                while ((n = await stream.ReadAsync(buf)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, n));
                    read += n;
                    if (total > 0) progress?.Report((double)read / total);
                }
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
            var pm = new PackageManager();
            var op = pm.AddPackageAsync(new Uri(msixPath), null,
                DeploymentOptions.ForceApplicationShutdown | DeploymentOptions.ForceTargetApplicationShutdown);
            var result = await op;
            if (result.ExtendedErrorCode != null)
            {
                App.LogStatic($"UpdateService: install error {result.ExtendedErrorCode.HResult:X}: {result.ErrorText}");
                return false;
            }
            App.LogStatic("UpdateService: install OK");
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
}
