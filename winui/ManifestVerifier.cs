using System;
using System.Text;
using NSec.Cryptography;

namespace MonitorTune;

// Ed25519 верификация подписи manifest'а.
// Public key зашит в UpdateService.ManifestPublicKeyForVerify (compile-time const, 32 байта base64).
// Private key — только в GitHub Actions Secret MANIFEST_ED25519_PRIVATE.
//
// Формат подписи: 64 байта сырой Ed25519 сигнатуры, base64-encoded, без переносов строк.
// Файл latest.json.sig содержит только эту base64-строку.
//
// Verify до deserialize JSON — если подпись невалидна, приложение НЕ читает поля manifest'а
// (защита от injection через невалидный JSON, plus обычная integrity check).
public static class ManifestVerifier
{
    /// <summary>Проверить подпись байтов manifest'а. Возвращает true если подпись валидна.</summary>
    public static bool Verify(byte[] manifestBytes, byte[] signatureBase64Bytes)
    {
        try
        {
            var pubKeyBase64 = UpdateService.ManifestPublicKeyForVerify;
            if (string.IsNullOrWhiteSpace(pubKeyBase64) || pubKeyBase64.StartsWith("REPLACE_WITH"))
            {
                App.LogStatic("ManifestVerifier: public key НЕ настроен — verify FAIL (это не production сборка)");
                return false;
            }

            byte[] pubKeyRaw;
            try { pubKeyRaw = Convert.FromBase64String(pubKeyBase64); }
            catch { App.LogStatic("ManifestVerifier: public key не декодируется как base64"); return false; }
            if (pubKeyRaw.Length != 32)
            {
                App.LogStatic($"ManifestVerifier: public key длина {pubKeyRaw.Length}, ожидалось 32");
                return false;
            }

            // Signature файл содержит base64 текст (возможно с trailing whitespace).
            byte[] sigRaw;
            try
            {
                var sigText = Encoding.UTF8.GetString(signatureBase64Bytes).Trim();
                sigRaw = Convert.FromBase64String(sigText);
            }
            catch (Exception ex)
            {
                App.LogStatic($"ManifestVerifier: sig не декодируется: {ex.Message}");
                return false;
            }
            if (sigRaw.Length != 64)
            {
                App.LogStatic($"ManifestVerifier: sig длина {sigRaw.Length}, ожидалось 64");
                return false;
            }

            var algo = SignatureAlgorithm.Ed25519;
            var pubKey = PublicKey.Import(algo, pubKeyRaw, KeyBlobFormat.RawPublicKey);
            bool ok = algo.Verify(pubKey, manifestBytes, sigRaw);
            return ok;
        }
        catch (Exception ex)
        {
            App.LogStatic($"ManifestVerifier.Verify ex: {ex.GetType().Name} {ex.Message}");
            return false;
        }
    }
}
