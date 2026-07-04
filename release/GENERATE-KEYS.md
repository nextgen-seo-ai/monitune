# Генерация ключей — пошаговая инструкция

**Читать перед первым push в GitHub.** Ключи генерируются на защищённой машине; приватники никогда не проходят через рабочие каналы (chat, email, git). Публичные ключи и thumbprints — попадают в код как константы.

## Что генерируется

1. **MSIX signing PFX** — самоподписанный сертификат для подписи MSIX-пакета. Пользователи импортируют публичный `.cer` при установке (это делает Inno setup.exe автоматически). Ключ используется CI при каждом release.
2. **Ed25519 keypair** — для подписи `latest.json` manifest'а. Public key зашивается в бинарь приложения; private key используется CI при подписи manifest'а на release.

Оба генерируются один раз. Ротация — раз в 2-3 года, с dual-signing периодом.

---

## Часть 1: MSIX signing PFX

**Запусти PowerShell на своей машине.** Не удалённо, не через RDP, желательно оффлайн.

### Шаг 1 — сгенерировать cert

```powershell
$cert = New-SelfSignedCertificate `
    -Type Custom `
    -Subject "CN=MonitorTune, O=nextgen-seo-ai, C=RU" `
    -KeyUsage DigitalSignature `
    -FriendlyName "MonitorTune Signing 2026" `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -NotAfter (Get-Date).AddYears(3) `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")

# Показать thumbprint — понадобится для константы в коде
Write-Host "THUMBPRINT: $($cert.Thumbprint)" -ForegroundColor Green

# И subject — для проверки
Write-Host "SUBJECT:    $($cert.Subject)"
```

Пример вывода:
```
THUMBPRINT: 5A9B3F82D6E7C4A1B8D9E0F2A3B4C5D6E7F8091A
SUBJECT:    CN=MonitorTune, O=nextgen-seo-ai, C=RU
```

Сохрани thumbprint — он пойдёт в `UpdateService.cs` как публичная константа.

### Шаг 2 — экспортировать в PFX с сильным паролем

```powershell
# Придумай сильный пароль (16+ символов, цифры+буквы+спец), НЕ используй пароли других сервисов
$pwdPlain = Read-Host -Prompt "Введи новый сильный пароль для PFX" -AsSecureString

Export-PfxCertificate `
    -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" `
    -FilePath "$HOME\Documents\MonitorTune-Signing.pfx" `
    -Password $pwdPlain | Out-Null

Write-Host "PFX сохранён: $HOME\Documents\MonitorTune-Signing.pfx"
```

**Заметь пароль надёжно** — например запись в KeePass под именем `MonitorTune Signing PFX 2026-2029`.

### Шаг 3 — экспортировать публичный .cer (для Inno setup.exe)

```powershell
Export-Certificate `
    -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" `
    -FilePath "$HOME\Documents\MonitorTune.cer" | Out-Null

Write-Host "Публичный .cer: $HOME\Documents\MonitorTune.cer (пойдёт в release/ для Inno)"
```

Файл `MonitorTune.cer` — публичный, ложится в `release/` рядом с Setup.iss. Его увидит Inno, импортирует пользователю при установке.

### Шаг 4 — загрузить PFX в GitHub Secrets

Открой https://github.com/nextgen-seo-ai/monitune/settings/secrets/actions

Нажми **New repository secret**, добавь два секрета:

```powershell
# Секрет 1: MSIX_SIGNING_PFX_BASE64
[Convert]::ToBase64String([IO.File]::ReadAllBytes("$HOME\Documents\MonitorTune-Signing.pfx")) | Set-Clipboard
# Ctrl+V в поле Secret value → Add secret
```

- **Name:** `MSIX_SIGNING_PFX_BASE64`
- **Value:** содержимое буфера (одна очень длинная base64-строка)

```powershell
# Секрет 2: MSIX_SIGNING_PASSWORD — пароль который ты придумал в шаге 2
```

- **Name:** `MSIX_SIGNING_PASSWORD`
- **Value:** пароль в plain text (GitHub его зашифрует)

### Шаг 5 — offline backup

```powershell
# Скопируй PFX + пароль в записи KeePass
# И скопируй PFX на USB-флешку, положи в сейф
Copy-Item "$HOME\Documents\MonitorTune-Signing.pfx" "E:\backup\MonitorTune-Signing.pfx"
```

### Шаг 6 — удалить приватник с рабочей машины

**Не удаляй пока не проверил что GitHub Secret работает** (первый успешный CI build). После этого:

```powershell
# Удалить локальную копию PFX (приватник теперь только в CI Secret + оффлайн backup)
Remove-Item "$HOME\Documents\MonitorTune-Signing.pfx" -Force
# Удалить cert из хранилища CurrentUser (публичный .cer остаётся в release/)
Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force
```

**Пока не удалил** — приватник на диске. Обращайся с файлом как с паролем.

---

## Часть 2: Ed25519 keypair (подпись manifest.json)

### Шаг 1 — установить утилиту

Нужна одна из двух опций. **Ed25519 через .NET (проще, кроссплатформенно):**

```powershell
# Установить NSec.Cryptography как утилиту генерации
# Простой способ — через готовый минимальный C# скрипт
mkdir "$HOME\Documents\ed25519-gen"
cd "$HOME\Documents\ed25519-gen"
```

Создай файл `gen.csx`:
```csharp
#r "nuget: NSec.Cryptography, 24.4.0"

using NSec.Cryptography;
using System;
using System.IO;

var algo = SignatureAlgorithm.Ed25519;
using var key = Key.Create(algo, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

var privRaw = key.Export(KeyBlobFormat.RawPrivateKey);
var pubRaw = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);

File.WriteAllText("private.b64", Convert.ToBase64String(privRaw));
File.WriteAllText("public.b64", Convert.ToBase64String(pubRaw));

Console.WriteLine("PRIVATE (для GH Secret MANIFEST_ED25519_PRIVATE):");
Console.WriteLine(Convert.ToBase64String(privRaw));
Console.WriteLine();
Console.WriteLine("PUBLIC (для константы в UpdateService.cs):");
Console.WriteLine(Convert.ToBase64String(pubRaw));
```

Запусти:
```powershell
dotnet script gen.csx
# Если dotnet-script нет: dotnet tool install --global dotnet-script
```

**Альтернатива — через OpenSSL:**

```powershell
# Требует openssl 1.1.1+
openssl genpkey -algorithm Ed25519 -out private.pem
openssl pkey -in private.pem -pubout -out public.pem

# Извлечь сырые байты в base64
# Приватник (последние 32 байта в DER после header)
# Публичник (последние 32 байта)
# Проще всё же C# способ — байты извлекаются корректно
```

### Шаг 2 — публичный ключ в код

Открой `winui/UpdateService.cs`. Найди константу:
```csharp
const string ManifestEd25519PublicKeyBase64 = "REPLACE_WITH_YOUR_PUBLIC_KEY_BASE64";
```

Замени `REPLACE_WITH_YOUR_PUBLIC_KEY_BASE64` на содержимое `public.b64` (короткая base64-строка, ~44 символа).

### Шаг 3 — приватник в GitHub Secret

Открой https://github.com/nextgen-seo-ai/monitune/settings/secrets/actions

- **Name:** `MANIFEST_ED25519_PRIVATE`
- **Value:** содержимое `private.b64` (одна base64-строка, ~44 символа)

### Шаг 4 — backup

```powershell
# Запись в KeePass: "MonitorTune Ed25519 Manifest Signing 2026-2029"
# Копия на USB в сейф
Copy-Item private.b64 "E:\backup\ed25519-private.b64"
```

### Шаг 5 — удалить приватник с рабочей машины

**После первого успешного CI build с подписью manifest'а:**

```powershell
cd "$HOME\Documents\ed25519-gen"
Remove-Item private.b64 -Force
# public.b64 можно оставить — это публичная информация
```

---

## Проверка что всё встало

После первого release (push тега v1.0.1):

1. Открой https://github.com/nextgen-seo-ai/monitune/actions — workflow должен пройти зелёным
2. Открой https://github.com/nextgen-seo-ai/monitune/releases/latest — должно быть 4 asset'а:
   - `MonitorTune-Setup.exe`
   - `MonitorTune-1.0.1-x64.msix`
   - `latest.json`
   - `latest.json.sig`
3. Скачай `latest.json` и `latest.json.sig`, проверь подпись через openssl:
   ```powershell
   # Извлечь public в PEM формат для openssl
   # (или через C# скрипт verify.csx — смотри release/verify-example.csx если есть)
   ```
4. Установи новый setup.exe, запусти MonitorTune — в трее должно быть уведомление об обновлении (если ставил старую версию)
5. Проверь что `Update` кнопка в трее — скачивает и ставит без ошибок

Если хотя бы один пункт не прошёл — **не удаляй приватники с локальной машины**, пока не пофиксишь.

---

## Что делать при утечке

**Если MSIX signing PFX утёк:**
1. Немедленно revoke: `Remove-Item "Cert:\CurrentUser\My\<thumbprint>"` (если ещё на машине), поменять GH Secret на пустое значение
2. Сгенерировать новый PFX (Часть 1 заново с новым паролем)
3. Обновить константу `AllowedSignerThumbprints[]` в `UpdateService.cs` — добавить НОВЫЙ thumbprint, старый пометить как revoked
4. Следующий release подписать **dual-sign** (обе подписи в одном MSIX) — существующие клиенты примут обе, новые тоже
5. Через 2-3 версии убрать старый thumbprint из константы

**Если Ed25519 private утёк:**
1. Сгенерировать новую пару
2. `UpdateService.cs` — переходный период: verify пробует новый key first, старый fallback (обе подписи проверяем на этапе миграции)
3. Через 1-2 версии убрать старый public

При любой утечке — **rotate немедленно**, не ждать.
