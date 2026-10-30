# Ручная проверка S2-UPDATES-INVENTORY: разбор MSU/CAB, пакеты образа, установка LCU

Статус: **Пройдена** (1dc2036e, Hyper-V, 30.10.2026).
Проверяет первую часть раздела 11 плана, для локальных пакетов:
- разбор .msu и .cab до DISM: KB, архитектура, SHA-1 из имени Catalog, что лежит внутри cabinet;
- список пакетов обслуживания образа (DismGetPackages);
- установку накопительного обновления (LCU) в офлайн-образ через DISM API и через Dism.exe, время и какие пакеты появились.

Это эксперимент: по нему выбирается рецепт для шага обновлений в конвейере. Работа идёт с **копией** `install.wim` в `C:\IF\scratch`; результат не сохраняется (`discard`).

## Сборка и требования

- `ImageForge.Experiments-<sha>` из CI этого PR, положить в `C:\IF`.
- Сервисная VM ([Hyper-V](HYPERV.md)) на чекпоинте `fixture-ready`, свободно не меньше 40 ГБ. `& $X mounts` → `none`.
- Накопительное обновление для фикстуры (Windows 11 26H2 x64). На [Microsoft Update Catalog](https://www.catalog.update.microsoft.com/) найти `Cumulative Update for Windows 11 Version 26H2 for x64-based Systems`, взять самое новое **без** слова Preview, нажать Download и скачать **все** файлы из окна (их может быть несколько: финальный LCU и checkpoint-пакеты). Имена не менять. Положить в `C:\IF\upd\lcu`. Можно скачать на хосте и скопировать в VM.

PowerShell от администратора, по одной строке:
```powershell
$X = { & "C:\IF\ImageForge.Experiments.exe" @args | Out-Host }
Start-Transcript C:\IF\s2-updates.txt
& $X version
& $X mounts
Get-ChildItem C:\IF\upd\lcu | Select-Object Name, Length
```

## Шаги

### 1. Разбор пакетов без DISM

```powershell
& $X update-scan C:\IF\upd\lcu
```
Ожидается: для каждого .msu строка `Msu | KB … | arch x64 | SHA-1 in name present`, список содержимого, у части строк `<- payload` (SSU-…cab, Windows11.0-KB…-x64.cab или .wim, .psf), строк `PROBLEM` нет, `RESULT: OK`. Содержимое MSU — главное, что нужно увидеть: пришлите весь вывод.

### 2. Отказы до DISM

```powershell
$M = (Get-ChildItem C:\IF\upd\lcu\*.msu | Sort-Object Length -Descending | Select-Object -First 1).FullName
New-Item -ItemType Directory C:\IF\upd\bad -Force | Out-Null
Copy-Item $M "C:\IF\upd\bad\windows11.0-kb9999999-x64_0000000000000000000000000000000000000000.msu"
Copy-Item $M "C:\IF\upd\bad\windows11.0-kb9999998-arm64.msu"
Set-Content C:\IF\upd\bad\notcab.msu "not a cabinet"
& $X update-scan C:\IF\upd\bad
```
Ожидается: у первого `PROBLEM: SHA-1 файла … не совпадает…`, у второго `PROBLEM: пакет для arm64…`, у третьего `PROBLEM: нет подписи MSCF…`; итог `with problems: 3`.

### 3. Пакеты образа до обновления

```powershell
New-Item -ItemType Directory C:\IF\scratch, C:\IF\scratch\mount -Force | Out-Null
Copy-Item C:\IF\fixture\media\sources\install.wim C:\IF\scratch\install.wim -Force
Set-ItemProperty C:\IF\scratch\install.wim -Name IsReadOnly -Value $false
& $X mount C:\IF\scratch\install.wim 1 C:\IF\scratch\mount
& $X package-list C:\IF\scratch\mount
```
Ожидается: `Packages read (DismGetPackages): N; … s`, строки пакетов, среди них `Package_for_RollupFix~…~26300.…` и `Package_for_ServicingStack~…`, итоги по типам и состояниям, `RESULT: OK`. Перепишите строки RollupFix и ServicingStack и время чтения.

### 4. LCU через DISM API

Берётся самый большой .msu из шага 2 (`$M`). Установка может идти 20–60 минут, окно не закрывать.
```powershell
& $X update-add C:\IF\scratch\mount $M api
& $X unmount C:\IF\scratch\mount discard
```
Ожидается: `add-package 0% … 100%`, `Add finished in … s`, строки `NEW:` (новый RollupFix, возможно ServicingStack) и `STATE:`, итог `new or changed: …`, `RESULT: OK`. Если DISM откажет (`DismAddPackage failed: … 0x…`), это тоже результат: перепишите строку целиком, затем всё равно выполните `unmount … discard`. Если в папке один .msu, после этого перейдите к шагу 6.

### 5. Папка через Dism.exe (если файлов в `C:\IF\upd\lcu` больше одного)

Так Microsoft советует ставить LCU с checkpoint-пакетами: DISM сам берёт нужные из папки.
```powershell
& $X mount C:\IF\scratch\install.wim 1 C:\IF\scratch\mount
& $X update-add C:\IF\scratch\mount C:\IF\upd\lcu cli
& $X unmount C:\IF\scratch\mount discard
```
Ожидается: вывод Dism.exe с процентами, `Dism.exe exit code 0` (или `3010` с пояснением), строки `NEW:`/`STATE:`, `RESULT: OK`.

### 6. Хост чистый

```powershell
& $X mounts
& $X host-check
Remove-Item C:\IF\scratch\install.wim -Force
Stop-Transcript
```
Ожидается: `mounts` → `none`, `host-check` → `RESULT: OK`.

### 7. Повтор после поддержки MSU в формате WIM

На b8d8cc5e утилита ждала, что MSU — это cabinet, а KB5134133 оказался WIM (`MSWIM`). Теперь WIM-MSU распознаётся: читаются заголовок и XML с образами, список файлов остаётся DISM.
```powershell
$X = { & "C:\IF\ImageForge.Experiments.exe" @args | Out-Host }
$M = "C:\IF\upd\lcu\Windows11.0-KB5134133-x64.msu"
Start-Transcript C:\IF\s2-updates-3.txt
& $X version
& $X update-scan C:\IF\upd\lcu
Copy-Item C:\IF\fixture\media\sources\install.wim C:\IF\scratch\install.wim -Force
Set-ItemProperty C:\IF\scratch\install.wim -Name IsReadOnly -Value $false
& $X mount C:\IF\scratch\install.wim 1 C:\IF\scratch\mount
& $X update-add C:\IF\scratch\mount $M api
& $X unmount C:\IF\scratch\mount discard
& $X mounts
& $X host-check
Remove-Item C:\IF\scratch\install.wim -Force
Stop-Transcript
```
Ожидается:
- `update-scan`: `Msu | KB 5134133 | arch x64 | … | Wim`, строка `WIM part 1/1, images N`, строки `image …`, без `PROBLEM`;
- `update-add … api`: проценты, `Add finished in … s` (около 10–15 минут), `NEW: …ServicingStack_9703…`, `NEW: …RollupFix…26100.9725…`, `STATE: … -> Superseded`, итог `new or changed: …` и `RESULT: OK`. Если DISM API откажет, перепишите строку `DismAddPackage failed: …` целиком;
- `mounts` → `none`, `host-check` → `RESULT: OK`.

## Что прислать

- файл `C:\IF\s2-updates.txt` целиком (или вывод по шагам);
- полный заголовок обновления из Catalog (название, KB, дата) и сколько файлов было в окне скачивания.

## Результаты

| BuildId | VM | Шаги | Итог |
|---|---|---|---|
| b8d8cc5e | Hyper-V | 0–5 | Частично. LCU: `Windows11.0-KB5134133-x64.msu` (26300.9725, 4,88 ГБ, один файл, в имени нет SHA-1). Шаг 1: файл не cabinet (нет `MSCF`), утилита отказала — формат MSU у нового LCU другой, разбор надо расширить. Шаг 2: три отказа верны (SHA-1, arm64, не cabinet). Шаг 3: 199 пакетов за 6 с, `RollupFix 26100.9457` Installed (и 26100.1742 Staged), `ServicingStack_9441 26100.9441`, `KB5121794 26100.8966`, `DotNetRollup_481`; 26H2 обслуживается в ветке 26100. Шаги 4–5 остановлены отказом утилиты; `mounts` → none, `host-check` → OK. Дальше: первые байты MSU и установка напрямую через Dism.exe. |
| b8d8cc5e | Hyper-V | 4 напрямую | Dism.exe 10.0.26100.8972 `/Add-Package` того же MSU: образ `10.0.26300.9457`, «Processing 1 of 1 - Expand», успешно, код 0, 11,6 мин. После: 289 пакетов (было 199), новые `ServicingStack_9703 26100.9703` и `RollupFix 26100.9725.1.2` Installed (тип Update), 90 пакетов FoD/языков обновлены до 26100.9711/9725, 89 прежних 26100.9457 стали `Superseded`. Значит, SSU внутри MSU и ставится тем же вызовом; один файл без checkpoint-пакетов. `mounts` → none, `host-check` → OK. |
| 1dc2036e | Hyper-V | 7 | Пройден. `update-scan`: KB5134133 — `Wim`, part 1/1, 1 образ `content`, 114 файлов, без проблем. `update-add … api` (DismAddPackage): 593 с (около 10 мин, быстрее Dism.exe — 11,6 мин), 199 → 289 пакетов, новые `ServicingStack_9703` и `RollupFix 26100.9725.1.2`, 89 пакетов `Installed -> Superseded`, итог `new or changed: 179`, `RESULT: OK`. Прогресс API после 100% уходит выше (2000%, 2072%): вторая фаза считается от другого Total — проценты теперь ограничены 100. `mounts` → none, `host-check` → OK. Вывод: LCU в формате WIM ставится одним DismAddPackage, SSU из того же файла; для шага обновлений годится DISM API. |
