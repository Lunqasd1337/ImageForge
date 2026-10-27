# Ручная проверка M1-EXP-A: DISM и запись маркера в офлайн-куст

Статус: **Пройдено** (27.10.2026, 26H2 Pro, BuildId `dd17b37`).
Покрывает эксперименты 1 и 3 из раздела 16 плана: ManagedDism (инвентаризация, mount/unmount, закрытие сессии, export WIM) и mount → загрузка SOFTWARE → запись → выгрузка → commit/export.
Эксперимент 2 (UAC/pipe через окно приложения) и установка результата в VM будут отдельным пакетом.

## Сборка

- Артефакт: `ImageForge.Experiments-<sha>` из GitHub Actions (джоба «Build all (Windows)» этого PR или `main`).
- Внутри: `ImageForge.Experiments.exe` (self-contained, .NET ставить не нужно) и `SHA256.txt` с хешем и BuildId.
- Перед запуском сверьте хеш: `Get-FileHash .\ImageForge.Experiments.exe -Algorithm SHA256` должен совпасть с `SHA256.txt`.

## Требования

**Сервисная VM** (на ней выполняются все команды ниже, «в гостевой Windows»):
- Hyper-V VM с Windows 11 24H2 или 25H2 x64, любая редакция, локальный администратор.
- Отдельная VM, не рабочая Windows хоста. Снимок (checkpoint) перед началом — рекомендуется.
- Свободно не меньше 30 ГБ на `C:` (NTFS).

**Фикстура:** официальный ISO Windows 11 26H2 x64 с microsoft.com (базовая фикстура M1 по решению владельца; см. `docs/m1.md`) (русский или английский — укажите в отчёте).

Все команды — в **PowerShell от имени администратора внутри сервисной VM**. Утилита сама просит повышение (UAC), если консоль не повышена.

## Шаги

Обозначения: `$X = "C:\IF\ImageForge.Experiments.exe"`. Вставляйте команды по одной строке: склеенная строка молча пропускает следующую команду. Каждая команда утилиты заканчивается строкой `RESULT: OK` или `RESULT: FAIL`.

### 0. Подготовка (в гостевой Windows)

```powershell
New-Item -ItemType Directory C:\IF, C:\IF\fixture, C:\IF\fixture\media, C:\IF\work, C:\IF\mount, C:\IF\out | Out-Null
# Скопируйте ImageForge.Experiments.exe в C:\IF и ISO в C:\IF\Win11_25H2.iso
$X = "C:\IF\ImageForge.Experiments.exe"
& $X version
```
Ожидается: BuildId, версия ОС, путь и версия `C:\Windows\System32\Dism.exe`, `RESULT: OK`.

### 1. Фикстура: одноиндексная WIM редакции Pro

```powershell
& $X hash C:\IF\Win11_25H2.iso
$iso = Mount-DiskImage C:\IF\Win11_25H2.iso -PassThru; $d = ($iso | Get-Volume).DriveLetter + ":"
Copy-Item "$d\*" C:\IF\fixture\media -Recurse
Dismount-DiskImage C:\IF\Win11_25H2.iso
# Файлы с ISO копируются с атрибутом «только чтение»; DISM не смонтирует такой WIM.
Get-ChildItem C:\IF\fixture\media -Recurse -File | ForEach-Object { $_.IsReadOnly = $false }
Get-ChildItem C:\IF\fixture\media\sources\install.*
```
Ожидается: в `sources` есть ровно один из файлов `install.wim` или `install.esd`.

```powershell
$src = (Get-ChildItem C:\IF\fixture\media\sources\install.* | Select-Object -First 1).FullName
& $X info $src
```
Ожидается: список индексов. Запишите номер индекса **Windows 11 Pro** (EditionId `Professional`) — дальше это `$pro`.

```powershell
$pro = <номер>
& $X export $src $pro C:\IF\fixture\install-pro.wim
Remove-Item $src -Force
Move-Item C:\IF\fixture\install-pro.wim C:\IF\fixture\media\sources\install.wim
& $X info C:\IF\fixture\media\sources\install.wim
& $X hash C:\IF\fixture\media\sources\install.wim
```
Ожидается: export завершается с `Dism.exe exit code 0`; `info` показывает **один** индекс (Index 1, Professional, версия `10.0.26300.x` для 26H2); записан SHA-256 фикстуры. Это хеш фикстуры для отчёта M1.

### 2. Эксперимент 1: mount/unmount без изменений

```powershell
Copy-Item C:\IF\fixture\media\sources\install.wim C:\IF\work\install.wim
& $X mount C:\IF\work\install.wim 1 C:\IF\mount
& $X mounts
& $X unmount C:\IF\mount discard
& $X mounts
& $X hash C:\IF\work\install.wim
```
Ожидается: mount с прогрессом; `mounts` показывает одну точку `C:\IF\mount`; после `unmount ... discard` — `DISM mounted images: none`; хеш рабочей копии равен хешу фикстуры из шага 1.

### 3. Эксперимент 3: запись маркера в офлайн-куст SOFTWARE

```powershell
& $X mount C:\IF\work\install.wim 1 C:\IF\mount
& $X write-marker C:\IF\mount
```
Ожидается:
- строка `Preview: HKLM\SOFTWARE\ImageForge\Validation -> offline Software hive (C:\IF\mount\Windows\System32\config\SOFTWARE), key ImageForge\Validation, Milestone = REG_SZ "M1"`;
- `Execution: Completed, steps 3/3`;
- `Loaded ImageForge hives: none`; `RESULT: OK`.

```powershell
& $X read-marker C:\IF\mount
& $X host-check
```
Ожидается: `Offline HKLM\SOFTWARE\ImageForge\Validation\Milestone = "M1"`; на хосте сервисной VM — `<absent>`; загруженных кустов ImageForge нет.

```powershell
& $X unmount C:\IF\mount commit
& $X mounts
& $X export C:\IF\work\install.wim 1 C:\IF\out\install.wim
& $X hash C:\IF\out\install.wim
```
Ожидается: commit без ошибок, `DISM mounted images: none`, export с кодом 0, записан SHA-256 результата.

### 4. Повторное открытие результата

```powershell
& $X mount C:\IF\out\install.wim 1 C:\IF\mount
& $X read-marker C:\IF\mount
& $X unmount C:\IF\mount discard
& $X mounts
& $X host-check
& $X hash C:\IF\fixture\media\sources\install.wim
```
Ожидается: маркер `"M1"` читается в экспортированном образе; после unmount активных mountpoint нет; на хосте маркера нет; хеш фикстуры **не изменился** с шага 1.

## Что прислать

Достаточно текста: «Пройдено» или «Не пройдено» и вывод консоли целиком (можно `Start-Transcript C:\IF\transcript.txt` перед шагом 0 и прислать файл).

При ошибке дополнительно:
- номер шага и полную команду;
- `C:\Windows\Logs\DISM\dism.log` (последние ~200 строк);
- вывод `& $X mounts` и `reg query HKLM` (список корней) после сбоя;
- версию ISO и язык.

Если после сбоя осталась точка монтирования, **не** запускайте `dism /Cleanup-Mountpoints` — пришлите вывод, решим вместе (утилита не трогает чужие монтирования).

## Результаты

| BuildId | Фикстура (SHA-256) | Сервисная VM | Шаги | Итог |
|---|---|---|---|---|
| `dd17b37` (exe `5E6B94D2…C05A567`) | 26H2 Pro x64, 10.0.26300.9457; SHA-256 — ожидается от владельца | VirtualBox, Windows 11 | 0–4 (шаг 2 повторён после склеенной команды) | Пройдено |
