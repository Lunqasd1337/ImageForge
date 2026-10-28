# Ручная проверка S2-APPX-INVENTORY: список и удаление предустановленных приложений

Статус: **Пройдена** (2026-10-28, Hyper-V, Win11 26H2 Pro).
Проверяет первую часть Appx (план, раздел 8): ImageForge читает список provisioned-пакетов образа через DISM, разбирает каждое имя пакета без потерь, предупреждает о Microsoft Store и App Installer и удаляет ровно один пакет по полному имени. Удаление в окне и в запуске появится в следующей части.
Работа идёт с **копией** `install.wim` в `C:\IF\scratch`, фикстура не монтируется.

## Сборка и требования

- `ImageForge.Experiments-<sha>` из CI этого PR, положить в `C:\IF`.
- Сервисная VM ([как подготовить в Hyper-V](HYPERV.md)) на чекпоинте `fixture-ready`, свободно не меньше 10 ГБ. `& $X mounts` → `none`.

PowerShell от администратора, по одной строке:
```powershell
$X = { & "C:\IF\ImageForge.Experiments.exe" @args | Out-Host }
Start-Transcript C:\IF\s2-appx.txt
& $X version
New-Item -ItemType Directory C:\IF\scratch, C:\IF\scratch\mount -Force | Out-Null
Copy-Item C:\IF\fixture\media\sources\install.wim C:\IF\scratch\install.wim
Set-ItemProperty C:\IF\scratch\install.wim -Name IsReadOnly -Value $false
& $X mount C:\IF\scratch\install.wim 1 C:\IF\scratch\mount
```
Ожидается: `RESULT: OK` у монтирования.

## Шаги

### 1. Список пакетов

```powershell
& $X appx-list C:\IF\scratch\mount
(dism /English /Image:C:\IF\scratch\mount /Get-ProvisionedAppxPackages | Select-String '^PackageName').Count
```
Ожидается:
- `RESULT: OK`, ни одной строки `PROBLEM:`.
- Число в `Provisioned packages: N` совпадает с числом из второй команды.
- У каждого пакета видны имя, версия, архитектура и семейство (`Имя_издатель`).
- Под `Microsoft.WindowsStore` и `Microsoft.DesktopAppInstaller` есть строка `WARNING:`, у остальных её нет.

### 2. Удаление одного пакета

Скопируйте полное имя Clipchamp из вывода DISM (если Clipchamp в образе нет, возьмите любой пакет без `WARNING:`, например `Microsoft.BingWeather`):
```powershell
$P = (dism /English /Image:C:\IF\scratch\mount /Get-ProvisionedAppxPackages | Select-String '^PackageName : (Clipchamp\.Clipchamp_.+)$').Matches[0].Groups[1].Value
$P
& $X appx-remove C:\IF\scratch\mount $P
```
Ожидается: DISM завершается с кодом 0, строка `Provisioned packages: N -> N-1; 'Clipchamp.Clipchamp' removed`, `RESULT: OK`.

### 3. Отказы без вызова DISM на удаление

```powershell
& $X appx-remove C:\IF\scratch\mount $P
& $X appx-remove C:\IF\scratch\mount 'Clipchamp.Clipchamp_1.0.0.0_neutral_~_yxz26nhyzhsrt /Quiet'
```
Ожидается:
- Первая команда: `... is not provisioned in this image; nothing was removed.` и `RESULT: FAIL`.
- Вторая: `... is not a valid package full name.` и `RESULT: FAIL`, строки `Remove-ProvisionedAppxPackage` нет.

### 4. Уборка

```powershell
& $X unmount C:\IF\scratch\mount discard
& $X mounts
& $X host-check
Stop-Transcript
```
Ожидается: размонтирование `RESULT: OK`, `mounts` → `none`, `host-check` → `RESULT: OK`.

## Что прислать

`C:\IF\s2-appx.txt` целиком. Если что-то не совпало, ещё вывод `dism /English /Image:C:\IF\scratch\mount /Get-ProvisionedAppxPackages` до уборки.

## Результаты

| BuildId | VM | Шаги | Итог |
|---|---|---|---|
| f90304ab | Hyper-V | 1–4 | OK: 47 пакетов, совпадает с DISM; предупреждения только у Store и App Installer; Clipchamp удалён (47 → 46); оба отказа без вызова удаления; mounts none, host-check OK |

Наблюдение: в provisioned-списке 26H2 Pro нет библиотек (VCLibs, UI.Xaml, NET.Native, WindowsAppRuntime); Store и App Installer — нейтральные пакеты-бандлы.
