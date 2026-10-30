# Ручная проверка S2-TARGETS-INVENTORY: boot.wim и WinRE фикстуры, обновления для них

Статус: **Ожидает проверки**.
Это эксперимент перед обслуживанием WinRE и boot.wim (план, раздел 11, «Рецепты по целям»). По его итогам выбирается рецепт:
- что лежит в `boot.wim` (индексы, загрузочный индекс) и во вложенном `Winre.wim`, какие у них версии и пакеты;
- ставится ли в WinRE накопительное обновление KB5134133 (ради SSU внутри него) и с какой ошибкой; Microsoft допускает `0x8007007e` на этом шаге;
- ставится ли Safe OS Dynamic Update в WinRE, что даёт очистка и насколько меняется размер;
- ставится ли LCU в индекс 2 `boot.wim` (установщик) и какие файлы установщика в нём новее, чем на носителе;
- что внутри Setup Dynamic Update.

Работа идёт с **копиями** в `C:\IF\scratch`; фикстура не меняется. Последовательность Microsoft — [Update Windows installation media with Dynamic Update](https://learn.microsoft.com/en-us/windows/deployment/update/media-dynamic-update).

## Сборка и требования

- `ImageForge.Experiments-<sha>` из CI этого PR, положить в `C:\IF`.
- Сервисная VM: можно в текущем состоянии, `C:\IF\fixture\media` и `C:\IF\upd\lcu\Windows11.0-KB5134133-x64.msu` на месте. Свободно не меньше 30 ГБ. `mounts` → `none`.
- С [Microsoft Update Catalog](https://www.catalog.update.microsoft.com/) скачать два пакета того же месяца, что и KB5134133, имена не менять:
  - `Safe OS Dynamic Update for Windows 11 Version 26H2 for x64-based Systems` → в `C:\IF\upd\safeos`;
  - `Setup Dynamic Update for Windows 11 Version 26H2 for x64-based Systems` → в `C:\IF\upd\setup`.
  Если для 26H2 их нет, взять для 24H2 (та же ветка 26100) и написать, какие взяты.

PowerShell от администратора:
```powershell
$X = { & "C:\IF\ImageForge.Experiments.exe" @args | Out-Host }
$M = "C:\IF\fixture\media"; $S = "C:\IF\scratch"; $L = "C:\IF\upd\lcu\Windows11.0-KB5134133-x64.msu"
Start-Transcript C:\IF\s2-targets.txt
& $X version
& $X mounts
New-Item -ItemType Directory $S, $S\mount -Force | Out-Null
Get-ChildItem C:\IF\upd\safeos, C:\IF\upd\setup | Select-Object FullName, Length
```

## Шаги

### 1. Состав boot.wim и install.wim

```powershell
& $X wim-header $M\sources\boot.wim
& $X info $M\sources\boot.wim
& $X wim-header $M\sources\install.wim
```
Ожидается: у `boot.wim` два образа (Windows PE и Windows Setup) и `boot index 2`; у `install.wim` `boot index 0`.

### 2. WinRE из install.wim

```powershell
Copy-Item $M\sources\install.wim $S\install.wim -Force
Set-ItemProperty $S\install.wim -Name IsReadOnly -Value $false
& $X mount $S\install.wim 1 $S\mount
Get-Item -Force $S\mount\Windows\System32\Recovery\Winre.wim | Select-Object FullName, Length, Attributes
Copy-Item -Force $S\mount\Windows\System32\Recovery\Winre.wim $S\winre.wim
& $X unmount $S\mount discard
Set-ItemProperty $S\winre.wim -Name Attributes -Value Normal
& $X hash $S\winre.wim
& $X wim-header $S\winre.wim
& $X info $S\winre.wim
```
Ожидается: `Winre.wim` около 600–800 МБ, один образ (`Microsoft Windows Recovery Environment`), версия 10.0.26100.…; запишите версию и SHA-256.

### 3. Пакеты обновлений

```powershell
& $X update-scan C:\IF\upd\safeos
& $X update-scan C:\IF\upd\setup
```
Ожидается: Safe OS DU — `Cab` с `update.mum`, без проблем. Setup DU тоже `.cab`, но это набор файлов установщика, а не пакет DISM, поэтому `PROBLEM` про `update.mum` здесь ожидаем — главное его содержимое.

### 4. WinRE: LCU, Safe OS DU, очистка

Ставится в копию `winre.wim`. LCU нужен ради стека обслуживания внутри него; Microsoft пишет, что на этом шаге возможна ошибка `0x8007007e` и её можно пропустить. Каждая установка — несколько минут.
```powershell
& $X mount $S\winre.wim 1 $S\mount
& $X package-list $S\mount
& $X update-add $S\mount $L api
$O = (Get-ChildItem C:\IF\upd\safeos\*.cab | Select-Object -First 1).FullName
& $X update-add $S\mount $O api
& $X cleanup $S\mount resetbase
& $X unmount $S\mount commit
& $X export $S\winre.wim 1 $S\winre-new.wim
Get-Item $S\winre.wim, $S\winre-new.wim | Select-Object Name, Length
& $X info $S\winre-new.wim
```
Ожидается и нужно переписать:
- `package-list` до: число пакетов и строки RollupFix/ServicingStack (если есть);
- `update-add … KB5134133`: `Add finished in … s` и `NEW:` либо `DismAddPackage failed: … 0x8007007E` — строку целиком;
- `update-add … safeos`: время и `NEW:`;
- `cleanup`: время и `packages … -> …`;
- размеры старого и нового `winre.wim`, версия из `info`.

### 5. boot.wim, индекс 2 (установщик): LCU

```powershell
Copy-Item $M\sources\boot.wim $S\boot.wim -Force
Set-ItemProperty $S\boot.wim -Name IsReadOnly -Value $false
& $X mount $S\boot.wim 2 $S\mount
& $X package-list $S\mount
& $X update-add $S\mount $L api
foreach ($f in "setup.exe", "setuphost.exe", "setupprep.exe") {
  foreach ($d in "$S\mount\sources", "$M\sources") {
    $i = Get-Item "$d\$f" -ErrorAction SilentlyContinue
    if ($i) { "{0} {1}" -f $i.FullName, $i.VersionInfo.FileVersion } else { "$d\$f missing" }
  }
}
& $X cleanup $S\mount resetbase
& $X unmount $S\mount discard
```
Ожидается: результат `update-add` (время, `NEW:` или строка ошибки), версии `setup.exe`/`setuphost.exe`/`setupprep.exe` в смонтированном образе и на носителе, итог `cleanup`. Образ не сохраняется (`discard`): здесь важна применимость, а не результат.

### 6. Setup Dynamic Update

```powershell
$T = "$S\setupdu"; Remove-Item $T -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory $T | Out-Null
$U = (Get-ChildItem C:\IF\upd\setup\*.cab | Select-Object -First 1).FullName
expand.exe $U -F:* $T | Out-Null
(Get-ChildItem $T -Recurse -File).Count
Get-ChildItem $T -Recurse -File | Sort-Object Length -Descending | Select-Object -First 15 FullName, Length, @{n='Ver';e={$_.VersionInfo.FileVersion}}
foreach ($f in "setup.exe", "setuphost.exe", "setupprep.exe") { $i = Get-ChildItem $T -Recurse -Filter $f | Select-Object -First 1; if ($i) { "{0} {1}" -f $i.FullName, $i.VersionInfo.FileVersion } else { "$f not in Setup DU" } }
```
Ожидается: число файлов, 15 самых больших и версии файлов установщика из Setup DU.

### 7. Уборка

```powershell
& $X mounts
& $X host-check
Remove-Item $S\install.wim, $S\boot.wim -Force
Stop-Transcript
```
Ожидается: `mounts` → `none`, `host-check` → `RESULT: OK`. `winre.wim` и `winre-new.wim` оставить до следующего шага.

## Что прислать

- файл `C:\IF\s2-targets.txt` целиком;
- точные названия скачанных Safe OS DU и Setup DU из Catalog (KB и дата).

## Результаты

| BuildId | VM | Шаги | Итог |
|---|---|---|---|
