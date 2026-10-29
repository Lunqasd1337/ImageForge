# Ручная проверка S2-FEATURES-INVENTORY: компоненты Windows и Capabilities

Статус: **Ожидает проверки**.
Проверяет первую часть Features/Capabilities (план, раздел 10): ImageForge читает через DISM список компонентов (Features) и Capabilities образа, разбирает каждое имя и состояние, показывает подробности, включает и выключает компонент без загрузки из Windows Update и удаляет одну Capability. Окно и запуск появятся в следующих частях. Неизвестно заранее, какие состояния печатает DISM на 26H2: шаги 1 и 2 это и показывают.
Работа идёт с **копией** `install.wim` в `C:\IF\scratch`, фикстура не монтируется.

## Сборка и требования

- `ImageForge.Experiments-<sha>` из CI этого PR, положить в `C:\IF`.
- Сервисная VM ([Hyper-V](HYPERV.md)) на чекпоинте `fixture-ready`, свободно не меньше 10 ГБ. `& $X mounts` → `none`.

PowerShell от администратора, по одной строке:
```powershell
$X = { & "C:\IF\ImageForge.Experiments.exe" @args | Out-Host }
Start-Transcript C:\IF\s2-features.txt
& $X version
New-Item -ItemType Directory C:\IF\scratch, C:\IF\scratch\mount -Force | Out-Null
Copy-Item C:\IF\fixture\media\sources\install.wim C:\IF\scratch\install.wim
Set-ItemProperty C:\IF\scratch\install.wim -Name IsReadOnly -Value $false
& $X mount C:\IF\scratch\install.wim 1 C:\IF\scratch\mount
```
Ожидается: `RESULT: OK` у монтирования.

## Шаги

### 1. Список компонентов

```powershell
& $X feature-list C:\IF\scratch\mount
(dism /English /Image:C:\IF\scratch\mount /Get-Features | Select-String '^Feature Name').Count
```
Ожидается:
- `RESULT: OK`, ни одной строки `PROBLEM:`.
- Число в `Features: N (...)` совпадает с числом из второй команды.
- `TelnetClient` в состоянии `Disabled`, `NetFx3` в `Disabled with Payload Removed`.
- Строки `UNKNOWN STATE:` допустимы: это состояния, которые ImageForge пока не различает. Перепишите их в ответ.

### 2. Список Capabilities

```powershell
& $X capability-list C:\IF\scratch\mount
(dism /English /Image:C:\IF\scratch\mount /Get-Capabilities | Select-String '^Capability Identity').Count
```
Ожидается: `RESULT: OK`, ни одной строки `PROBLEM:`, число в `Capabilities: N (...)` совпадает с числом из второй команды. Строки `UNKNOWN STATE:` тоже перепишите.

### 3. Подробности

```powershell
& $X feature-info C:\IF\scratch\mount NetFx3
& $X capability-info C:\IF\scratch\mount OpenSSH.Client~~~~0.0.1.0
```
Ожидается: обе `RESULT: OK`, у NetFx3 есть `Display Name`, `Description`, `Restart Required` и `State`. Русские названия и описания читаются, без `?` вместо букв (`version` печатает `Dism.exe output read as code page 866`). Если `OpenSSH.Client~~~~0.0.1.0` нет в списке шага 2, возьмите любое имя оттуда.

### 4. Включить и выключить компонент

```powershell
& $X feature-enable C:\IF\scratch\mount TelnetClient
& $X feature-disable C:\IF\scratch\mount TelnetClient
```
Ожидается:
- Первая: в строке DISM есть `/LimitAccess` и нет `/All`, итог `Feature 'TelnetClient': Disabled -> Enabled` (или `Enable Pending`), `RESULT: OK`.
- Вторая: `Enabled -> Disabled` (или `Disable Pending`), `RESULT: OK`.

### 5. Компонент без источника не включается

```powershell
& $X feature-enable C:\IF\scratch\mount NetFx3
```
Ожидается: DISM завершается с ошибкой (обычно `0x800f081f`, источник не найден), `RESULT: FAIL`. Обращения к Windows Update нет: в строке DISM есть `/LimitAccess`.

### 6. Удаление одной Capability

Берётся первая установленная из списка Capabilities, без которых образ обойдётся (на 26H2 `Print.Fax.Scan` уже нет):
```powershell
$I = Get-WindowsCapability -Path C:\IF\scratch\mount | Where-Object State -eq Installed | ForEach-Object Name
$C = 'MathRecognizer~~~~0.0.1.0', 'Microsoft.Windows.WordPad~~~~0.0.1.0', 'App.StepsRecorder~~~~0.0.1.0', 'OpenSSH.Client~~~~0.0.1.0' | Where-Object { $_ -in $I } | Select-Object -First 1
$C
& $X capability-remove C:\IF\scratch\mount $C
```
Ожидается: `$C` не пустая, DISM с кодом 0, строка `Capability '...': Installed -> Not Present` (или `Uninstall Pending`), `RESULT: OK`.

### 7. Отказы без вызова DISM на изменение

```powershell
& $X capability-remove C:\IF\scratch\mount $C
& $X feature-enable C:\IF\scratch\mount 'TelnetClient /All'
& $X capability-remove C:\IF\scratch\mount 'OpenSSH.Client~~~~0.0.1.0 /Quiet'
```
Ожидается:
- Первая: `... is not installed in this image; nothing was removed.`, `RESULT: FAIL`.
- Вторая и третья: `... is not a valid feature name.` / `... is not a valid capability name.`, `RESULT: FAIL`, строки с Dism.exe нет.

### 8. Уборка

```powershell
& $X unmount C:\IF\scratch\mount discard
& $X mounts
& $X host-check
Stop-Transcript
```
Ожидается: размонтирование `RESULT: OK`, `mounts` → `none`, `host-check` → `RESULT: OK`.

## Что прислать

`C:\IF\s2-features.txt` целиком.

## Результаты

| BuildId | VM | Шаги | Итог |
|---|---|---|---|
| b3923677 | Hyper-V | 1, 3–5, 7 | Пройдены: 136 компонентов, совпадает с DISM, неизвестных состояний нет; TelnetClient включается и выключается; NetFx3 без источника — `0x800f0912`, `RESULT: FAIL`; отказы на имена с `/All` и `/Quiet`. Не пройдено: шаг 2 не запускался; в шаге 6 `Print.Fax.Scan` нет в образе; русские названия в шаге 3 пришли как `?`: вывод DISM читался как UTF-8, а он в OEM-кодировке (866). Исправлено: вывод читается в OEM-кодировке системы, шаг 6 выбирает Capability из установленных. |
