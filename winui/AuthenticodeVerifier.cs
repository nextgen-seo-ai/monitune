using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace MonitorTune;

// Authenticode/AppX signature verification через WinVerifyTrust (wintrust.dll)
// + извлечение thumbprint издателя через X509Certificate.
//
// Вызывается в UpdateService.DownloadAndInstallAsync ДО AddPackageAsync — чтобы
// обнаружить подмену/битую подпись до install (иначе AddPackageAsync упадёт с
// невнятной ошибкой, а мы не узнаем thumbprint для whitelist проверки).
public static class AuthenticodeVerifier
{
    // ── Native constants / structs ─────────────────────────────────

    // WINTRUST_ACTION_GENERIC_VERIFY_V2 — стандартный action для файловой Authenticode проверки.
    static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
        new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    const uint WTD_UI_NONE = 2;
    const uint WTD_REVOKE_WHOLECHAIN = 1;
    const uint WTD_CHOICE_FILE = 1;
    const uint WTD_STATEACTION_VERIFY = 1;
    const uint WTD_STATEACTION_CLOSE = 2;
    const uint WTD_REVOCATION_CHECK_CHAIN = 0x00000040;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    /// <summary>Проверить Authenticode подпись файла (MSIX / EXE / DLL).
    /// Возвращает: (ok — подпись валидна, thumbprint — SHA-1 signer'а в hex верхнем регистре или null).</summary>
    public static (bool ok, string? thumbprint) VerifyMsix(string filePath)
    {
        // MSIX-пакеты подписываются как zip-container с CI.cat или встроенным AppxSignature.p7x.
        // WinVerifyTrust корректно обрабатывает оба формата через тот же GENERIC_VERIFY_V2 action.
        uint result = uint.MaxValue;
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = Marshal.StringToHGlobalUni(filePath),
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero,
        };
        IntPtr pFileInfo = IntPtr.Zero;
        try
        {
            pFileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(fileInfo, pFileInfo, false);

            var wtd = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_WHOLECHAIN,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = pFileInfo,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_REVOCATION_CHECK_CHAIN,
            };
            var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            result = WinVerifyTrust(IntPtr.Zero, ref action, ref wtd);

            // Обязательно закрыть state (иначе утечка crypto handles).
            wtd.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, ref action, ref wtd);
        }
        finally
        {
            if (fileInfo.pcwszFilePath != IntPtr.Zero) Marshal.FreeHGlobal(fileInfo.pcwszFilePath);
            if (pFileInfo != IntPtr.Zero) Marshal.FreeHGlobal(pFileInfo);
        }

        bool ok = result == 0;
        App.LogStatic($"AuthenticodeVerifier: WinVerifyTrust HRESULT=0x{result:X8} ({(ok ? "OK" : "FAIL")})");

        // Извлечь thumbprint издателя. Для MSIX подпись лежит НЕ как PE WIN_CERTIFICATE,
        // а в отдельном файле /AppxSignature.p7x внутри ZIP-контейнера MSIX.
        // Формат: 4-байтовая сигнатура "PKCX" (magic) + сырое PKCS#7 (SignedCms).
        // CreateFromSignedFile работает для PE, но на MSIX возвращает мусор или CryptographicException.
        // Правильный путь: распаковать ZIP → взять AppxSignature.p7x → отбросить magic → SignedCms.Decode → Signer[0].Certificate.
        string? thumbprint = null;
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(filePath);
            var sigEntry = zip.GetEntry("AppxSignature.p7x");
            if (sigEntry != null)
            {
                using var s = sigEntry.Open();
                using var ms = new System.IO.MemoryStream();
                s.CopyTo(ms);
                var raw = ms.ToArray();
                if (raw.Length > 4)
                {
                    // Пропускаем 4-байтовый "PKCX" magic — дальше сырое PKCS#7.
                    var pkcs7 = raw.AsSpan(4).ToArray();
                    var cms = new System.Security.Cryptography.Pkcs.SignedCms();
                    cms.Decode(pkcs7);
                    if (cms.SignerInfos.Count > 0)
                    {
                        thumbprint = cms.SignerInfos[0].Certificate?.Thumbprint;
                    }
                }
            }
            else
            {
                App.LogStatic("AuthenticodeVerifier: AppxSignature.p7x нет в MSIX-архиве");
            }
        }
        catch (Exception ex)
        {
            App.LogStatic($"AuthenticodeVerifier: AppxSignature.p7x парсинг ex: {ex.GetType().Name} {ex.Message}");
        }

        return (ok, thumbprint);
    }
}
