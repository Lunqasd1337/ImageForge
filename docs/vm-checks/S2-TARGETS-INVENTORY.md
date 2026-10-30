# Ручная проверка S2-TARGETS-INVENTORY: boot.wim и WinRE фикстуры, обновления для них

Статус: **Пройдена** (4c76a56d, Hyper-V, 30.10.2026). Пакеты: Safe OS DU KB5130881 (26H2/25H2, 209 МБ), Setup DU KB5128977 (2026-10, 25H2).
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
| 4c76a56d | Hyper-V | 1 | Пройден. boot.wim 597 МБ: 2 образа, `boot index 2` — 1 «Microsoft Windows PE (amd64)», 2 «Microsoft Windows Setup (amd64)», оба 10.0.26100.9457. install.wim: 5 редакций, `boot index 0` — 1 Education, 2 Enterprise, 3 Pro, 4 Pro Education, 5 Pro for Workstations. Конвейер сейчас берёт индекс 1 (Education). |
| 4c76a56d | Hyper-V | 2 | Пройден. `Windows\System32\Recovery\Winre.wim` 803 275 696 байт (атрибуты Archive, SparseFile, ReparsePoint — файл из WOF-сжатия образа, копируется обычным Copy-Item), SHA-256 5CD5CA1D…760A0, 1 образ «Microsoft Windows Recovery Environment (amd64)», `boot index 1`, 10.0.26100.9444. |
| 4c76a56d | Hyper-V | 3 | Пройден. KB5130881 — Cab, 7618 записей (компоненты 10.0.26100.9817, winre-tools, bootux-winre, recoveryagent, cloudrec), без проблем. KB5128977 — Cab, 169 файлов, `PROBLEM: в CAB нет update.mum` — ожидаемо, это файлы установщика. |
| 4c76a56d | Hyper-V | 4 | Пройден. WinRE до: 56 пакетов, `SafeOSDU 26100.9444.1.29`, `ServicingStack_9441`, `KB5121794`, язык ru-RU. LCU KB5134133 через DismAddPackage: 56 с, без ошибки (0x8007007E не было), добавился только `ServicingStack_9703 26100.9703.1.4` — полный LCU в WinRE не ставится, как и ожидалось. Safe OS DU: 51 с, `SafeOSDU 26100.9444.1.29` → `26100.9817.1.9`. Cleanup /ResetBase: 3 с, 57 → 56 пакетов. После commit winre.wim 1 147 МБ, после export 841 МБ (исходный 803 МБ); версия 10.0.26100.9817. Export обязателен: без него WinRE на 43 % больше. |
| 4c76a56d | Hyper-V | 5 | Пройден. boot.wim индекс 2 до: 34 пакета, `RollupFix 26100.9457`, `ServicingStack_9441`. LCU: 162 с, `RollupFix 26100.9725.1.2`, `ServicingStack_9703`, WinPE-Package и языковой пакет ru-RU → 10.0.26100.9725, всего 63 изменения, прежний RollupFix ушёл. Файлы установщика в образе новее носителя: setup.exe 9549 (носитель 26100.1), setuphost.exe 9725 (9445), setupprep.exe 8521 (26100.1). Cleanup /ResetBase: 3 с, 35 → 34. |
| 4c76a56d | Hyper-V | 6 | Пройден. Setup DU: 169 файлов в корне и `replacementmanifests\…` — SetupPlatform.dll, Appraiser.dll, SetupCore.dll, appraiser.sdb, SetupPrep.exe и SetupHost.exe 10.0.26100.9808 (новее, чем в boot.wim после LCU: 9725 и 8521), DismApi.dll, ReAgent.dll; setup.exe в нём нет. |
| 4c76a56d | Hyper-V | 7 | Пройден. `mounts` → none, `host-check` → OK. |

## Вывод для рецепта

- **WinRE** (для каждого install-образа, обычно один и тот же файл): извлечь `Winre.wim` → LCU MSU (даёт только SSU) → Safe OS DU → cleanup /ResetBase → commit → export → положить обратно в `Windows\System32\Recovery`. Около 2 мин работы DISM; ошибки 0x8007007E на 26H2 нет, но допуск для неё на шаге SSU оставить по плану.
- **boot.wim**: оба индекса — LCU → cleanup /ResetBase → commit; затем export всех индексов с сохранением `boot index 2`. Из индекса 2 скопировать на носитель обновлённые файлы установщика из `sources` (setup.exe и др.).
- **Носитель**: Setup DU распаковать поверх `sources` носителя последним: его SetupHost/SetupPrep новее, чем в boot.wim.
