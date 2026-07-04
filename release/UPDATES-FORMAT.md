# Auto-update манифест — формат и схема безопасности

## Куда публикуется

**Primary:** GitHub Releases репозитория `nextgen-seo-ai/monitune`.
На каждый релиз (tag `v1.0.1`, `v1.0.2` и т.д.) уложены 4 asset'а:

- `MonitorTune-Setup.exe` — Inno-инсталлятор
- `MonitorTune-{ver}-x64.msix` — MSIX-пакет
- `latest.json` — манифест обновления (см. схему ниже)
- `latest.json.sig` — Ed25519 подпись `latest.json`, отдельным файлом

Приложение читает `https://github.com/nextgen-seo-ai/monitune/releases/latest/download/latest.json` — GitHub всегда отдаёт файл из последнего release, без раскрытия конкретной версии в URL.

**Fallback:** приватные mesh-узлы. Их URL зашиты в бинарь `UpdateService.cs` как `PrivateFallbackManifests` (compile-time const). НЕ хранятся в `settings.json`, НЕ упоминаются в landing/README. Приложение переключается на них только если все попытки GitHub провалились (timeout, DNS, 5xx).

## Формат manifest'а (`latest.json`)

```json
{
  "version":     "1.0.1.0",
  "msix":        "https://github.com/nextgen-seo-ai/monitune/releases/download/v1.0.1/MonitorTune-1.0.1.0-x64.msix",
  "setup":       "https://github.com/nextgen-seo-ai/monitune/releases/download/v1.0.1/MonitorTune-Setup.exe",
  "sha256":      "ABCDEF0123456789...",
  "min_version": "1.0.0.0",
  "notes":       "Что нового в этой версии",
  "mandatory":   false,
  "released":    "2026-07-05T12:00:00Z"
}
```

**Поля:**

- `version` — 4-компонентная (Major.Minor.Build.Revision), должна совпадать с версией внутри MSIX
- `msix` — прямая ссылка на MSIX (auto-update)
- `setup` — прямая ссылка на setup.exe (для landing и ручной установки)
- `sha256` — HEX-хэш **MSIX-файла** (не setup.exe), обязательно к проверке
- `min_version` — минимальная версия с которой разрешён downgrade. Приложение отказывается ставить `version < currentVersion` если пользователь не выставил `AllowDowngrade` в Settings
- `notes` — что нового (показывается в tray-уведомлении и в SettingsWindow)
- `mandatory` — если true, приложение блокирует работу пока не обновится (не используется в v1)
- `released` — ISO-8601 timestamp

## Подпись: `latest.json.sig`

Отдельный файл рядом с `latest.json`. Содержит Ed25519 подпись байтов `latest.json`.

Формат: 64 байта сырой Ed25519 сигнатуры, base64-encoded, без переносов строк.

**Приложение проверяет подпись до парсинга JSON:**
1. Скачивает `latest.json.sig` и `latest.json`
2. `ManifestVerifier.Verify(manifestBytes, sigBytes)` с зашитым Ed25519 public key
3. Если verify провалилось → лог + skip (не переходит к содержимому)
4. Если ok → parse JSON, compare version, etc.

**Public key** зашит в `UpdateService.cs` как base64 const. Ротация ключа = compile new release с новым pub key + подписать переходный manifest старым ключом (dual-signing period).

**Private key** хранится ТОЛЬКО в GitHub Actions Secret `MANIFEST_ED25519_PRIVATE`. Никогда не на dev-машине, никогда в repo. Backup — оффлайн в KeePass + USB в сейф.

## Проверка целостности MSIX

После скачивания MSIX по URL из manifest'а:

1. **SHA-256** — сверяем с `manifest.sha256`. Mismatch → удалить файл, прервать
2. **WinVerifyTrust** — вызываем `wintrust.dll::WinVerifyTrust` с action `WINTRUST_ACTION_GENERIC_VERIFY_V2` на скачанный .msix. Проверяем что подпись валидна и цепочка ведёт к нашему CA
3. **Thumbprint check** — сверяем thumbprint издателя с зашитой в бинарь константой `AllowedSignerThumbprints[]`. Если не наш — abort до `AddPackageAsync`
4. **PackageManager.AddPackageAsync** — Windows сам ещё раз проверит подпись при установке

## Схема генерации ключей (шаги для владельца repo)

**Ed25519 pair** — генерируется один раз, private лежит в CI, public зашивается в код:

```powershell
# Локально, на защищённой машине (не на dev-workstation используемой для code):
dotnet tool install --global dotnet-ed25519
dotnet ed25519 generate --output keys/
# → keys/private.b64 (64 байта), keys/public.b64 (32 байта)
```

Или через openssl:
```bash
openssl genpkey -algorithm Ed25519 -out private.pem
openssl pkey -in private.pem -pubout -out public.pem
# base64-версии
openssl pkey -in private.pem -text -noout   # приватник в HEX
openssl pkey -in public.pem -pubin -text -noout   # публичный в HEX
```

- **Public** → в `UpdateService.cs` как `const string Ed25519PublicKeyBase64`
- **Private** → в GitHub → Settings → Secrets → Actions → New → `MANIFEST_ED25519_PRIVATE` (base64)

**MSIX signing PFX** — генерируется через PowerShell:

```powershell
# На защищённой машине, offline
$cert = New-SelfSignedCertificate `
    -Type Custom -Subject "CN=MonitorTune, O=nextgen-seo-ai" `
    -KeyUsage DigitalSignature -FriendlyName "MonitorTune Signing" `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
$pwd = ConvertTo-SecureString -String "СИЛЬНЫЙ_ПАРОЛЬ_ЗДЕСЬ" -Force -AsPlainText
Export-PfxCertificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath "MonitorTune-Signing.pfx" -Password $pwd

# Тhumbprint в буфер (для константы в коде)
$cert.Thumbprint | Set-Clipboard
```

- **Thumbprint** → в `UpdateService.cs` как `AllowedSignerThumbprints[]`
- **PFX (base64)** → GitHub Secret `MSIX_SIGNING_PFX_BASE64`:
  ```powershell
  [Convert]::ToBase64String([IO.File]::ReadAllBytes("MonitorTune-Signing.pfx")) | Set-Clipboard
  ```
- **Password** → GitHub Secret `MSIX_SIGNING_PASSWORD`

**Никогда не показывай эти base64 в чат/PR/screenshots** — они содержат приватный ключ.

## Downgrade protection

Приложение отклоняет установку manifest'а если:
- `manifest.version <= currentRunningVersion`, И
- `Settings.AllowDowngrade == false`

Флаг `AllowDowngrade` доступен только через прямую правку `settings.json` — намеренно не выводится в UI, чтобы обычный пользователь не отключил защиту случайно.

## Что публично, что приватно

| Артефакт | Публично | Приватно |
|----------|----------|----------|
| GitHub Release URL | ✅ landing, README | — |
| MSIX / setup.exe / latest.json / .sig | ✅ GitHub Releases | — |
| Ed25519 public key | ✅ (константа в бинаре) | — |
| Ed25519 private key | ❌ | GH Secret + offline backup |
| MSIX signing thumbprint | ✅ (константа в бинаре) | — |
| MSIX signing PFX | ❌ | GH Secret + offline backup |
| Mesh fallback URL'ы | ❌ (compile-time const в бинаре) | — |
| Telemetry endpoint | ❌ (compile-time const, opt-in только) | — |
