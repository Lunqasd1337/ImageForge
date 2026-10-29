# Ручная проверка S2-UPDATES-INVENTORY: разбор MSU/CAB, пакеты образа, установка LCU

Статус: **Ожидает проверки**.
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

## Что прислать

- файл `C:\IF\s2-updates.txt` целиком (или вывод по шагам);
- полный заголовок обновления из Catalog (название, KB, дата) и сколько файлов было в окне скачивания.

## Результаты

| BuildId | VM | Шаги | Итог |
|---|---|---|---|
