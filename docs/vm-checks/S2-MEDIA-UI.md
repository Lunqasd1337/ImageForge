# Ручная проверка S2-MEDIA-UI: Setup DU и загрузчик носителя

Статус: **Ожидает проверки**.
Проверяет пункты 2 и 3 сверки с планом (раздел 11, строка Media). Порядок взят из [сценария Microsoft](https://learn.microsoft.com/en-us/windows/deployment/update/media-dynamic-update), шаги 26–28.
- Setup Dynamic Update распознаётся в папке обновлений. Его можно отметить только «На носитель (Setup DU)», в образ его поставить нельзя.
- Шаг запуска `media` идёт после `boot`:
  1. Setup DU распаковывается (`expand.exe -F:*`) в `sources`.
  2. Поверх него из обновлённого boot.wim (образ 2) кладутся `setup.exe` и `setuphost.exe`.
  3. Загрузчик меняется по карте:
     - `bootmgfw.efi` на место каждого `bootmgfw.efi`, `bootx64.efi`, `bootia32.efi`, `bootaa64.efi` носителя;
     - `bootmgr.efi` на место каждого `bootmgr.efi`;
     - `boot.stl` в `efi\microsoft\boot`.
- Файлы, которых в источнике нет, не создаются (кроме `boot.stl`). Файл с версией старше, чем был на носителе, не ставится, в журнале строка `kept`.
- `efisys.bin` и `bootmgr` (BIOS) не меняются, как и в сценарии Microsoft.

Важно: в S2-TARGETS-INVENTORY у SetupHost.exe из Setup DU версия 9808, а в boot.wim после LCU — 9725. По сценарию Microsoft на носителе остаётся файл из boot.wim (9725), чтобы он совпадал с программой установки в boot.wim. Остальные файлы Setup DU (SetupPrep.exe 9808, SetupCore.dll и т. д.) остаются.

## Сборка

- Из одной сборки CI этого PR: `ImageForge.App-<sha>` и `ImageForge.Experiments-<sha>`.
- `C:\IF\app` удалить и распаковать туда новый `ImageForge.App`; `ImageForge.Experiments.exe` положить в `C:\IF`.

## Требования

- Сервисная VM, фикстура `C:\IF\fixture\media`. Свободно не меньше 40 ГБ.
- `C:\IF\upd\lcu\Windows11.0-KB5134133-x64.msu` и Setup DU KB5128977 в `C:\IF\upd\setup` (из S2-TARGETS-INVENTORY).
- Для шага 6: Windows ADK (Deployment Tools) на сервисной VM и VirtualBox на хосте, как в [M1-INSTALL](M1-INSTALL.md).

PowerShell от администратора:
```powershell
$E = "C:\IF\ImageForge.Experiments.exe"; $X = { & $E @args | Out-Host }
Start-Transcript C:\IF\s2-media-ui.txt
& $X version
& $X mounts
Get-ChildItem C:\IF\upd -Recurse -File | Select-Object FullName, Length
Get-ChildItem C:\IF\fixture\media -Recurse -Force -Include b*.efi, boot.stl, bootmgr | ForEach-Object { "{0} {1}" -f $_.FullName, $_.VersionInfo.FileVersion }
```
Ожидается: `mounts` → `none`; в `C:\IF\upd` есть LCU и `setup\windows11.0-kb5128977-x64.cab` (имя может быть длиннее). Перепишите список файлов загрузчика фикстуры.

## Шаги

### 1. Окно

Запустить `C:\IF\app\ImageForge.App.exe`, «Подключить движок», в UAC — «Да». Источник `C:\IF\fixture\media`, рабочая папка `C:\IF\ws-media`, результат `C:\IF\result-media`. «Прочитать образ».
Ожидается: в журнале `протокол 16`.

### 2. Отметки и превью

Папка обновлений `C:\IF\upd`, «Найти обновления», раскрыть «Найденные обновления».
Ожидается:
- у каждого файла четыре отметки: «В образ», «В WinRE», «В boot.wim», «На носитель (Setup DU)»;
- у KB5128977 во второй строке `Setup DU`, под ней «Setup Dynamic Update: файлы программы установки, не пакет DISM…». У него доступна только «На носитель», у остальных она недоступна.

Отметить у KB5128977 «На носитель», больше ничего, «Подготовить».
Ожидается: «План готов… Setup DU на носитель.», в превью строка «Носитель: распаковать Setup DU в sources…», «Распаковать setup\…kb5128977…: KB5128977, CAB, Setup DU» и «Внимание: boot.wim не обновляется…».

Отметить ещё у KB5134133 «В boot.wim», «Подготовить».
Ожидается: предупреждения про boot.wim нет. Есть блок boot.wim со строкой «…на носитель из него идут setup.exe и setuphost.exe (в sources) и загрузчик…», потом блок «Носитель». Статус «…В boot.wim обновлений: 1, драйверов: 0. Setup DU на носитель.».

Пришлите снимок превью.

### 3. Запуск

«Запустить», окно не закрывать (около 25 минут).
Ожидается в журнале:
- `staged 2 update package(s)` до `mount...`;
- шаг `boot...` как в S2-BOOT-UI, в конце `boot.wim: … MB -> … MB, boot index 2`;
- шаг `media...`:
  - `setup du …KB5128977…: expand` и `expand.exe … -F:* …\setup-du`;
  - `setup du: 169 files into sources, … of them replace media files` (число около того);
  - `setup file setup.exe: media … -> boot.wim …` и `setup file setuphost.exe: …`;
  - `boot file efi\boot\bootx64.efi: media … -> boot.wim …`, `boot file bootmgr.efi: …` и другие `b*.efi` носителя, `boot file efi\microsoft\boot\boot.stl: …`;
  - строк `kept` нет;
- «Готово. Результат сохранён.».

Перепишите строки шага `media...` целиком и время шагов `boot` и `media`.

### 4. Сверка результата

```powershell
$R = "C:\IF\result-media"; $S = "C:\IF\scratch"; $F = "C:\IF\fixture\media"
New-Item -ItemType Directory $S\mount -Force | Out-Null
Copy-Item -Force $R\sources\boot.wim $S\boot-m.wim
Set-ItemProperty $S\boot-m.wim -Name Attributes -Value Normal
& $X mount $S\boot-m.wim 2 $S\mount
$pairs = @(
  @("$S\mount\sources\setup.exe", "$R\sources\setup.exe"),
  @("$S\mount\sources\setuphost.exe", "$R\sources\setuphost.exe"),
  @("$S\mount\Windows\Boot\EFI\bootmgfw.efi", "$R\efi\boot\bootx64.efi"),
  @("$S\mount\Windows\Boot\EFI\bootmgr.efi", "$R\bootmgr.efi"),
  @("$S\mount\Windows\Boot\EFI\boot.stl", "$R\efi\microsoft\boot\boot.stl")
)
foreach ($p in $pairs) {
  $a = (Get-FileHash $p[0]).Hash; $b = if (Test-Path $p[1]) { (Get-FileHash $p[1]).Hash } else { "missing" }
  "{0} -> {1}: {2}" -f $p[0], $p[1], ($(if ($a -eq $b) { "SAME" } else { "DIFFERENT" }))
}
& $X unmount $S\mount discard
Remove-Item $S\boot-m.wim -Force
foreach ($f in "sources\setupprep.exe", "sources\setuphost.exe", "sources\setup.exe", "efi\boot\bootx64.efi", "bootmgr.efi") {
  foreach ($d in $F, $R) { $i = Get-Item "$d\$f" -ErrorAction SilentlyContinue; "{0} {1}" -f "$d\$f", $(if ($i) { $i.VersionInfo.FileVersion } else { "missing" }) }
}
(Get-FileHash $F\efi\microsoft\boot\efisys.bin).Hash -eq (Get-FileHash $R\efi\microsoft\boot\efisys.bin).Hash
(Get-FileHash $F\bootmgr).Hash -eq (Get-FileHash $R\bootmgr).Hash
Get-ChildItem $R\sources -File | Measure-Object | Select-Object Count
Get-ChildItem $F\sources -File | Measure-Object | Select-Object Count
```
Ожидается:
- все пять пар `SAME`;
- `setupprep.exe` в результате 10.0.26100.9808 (из Setup DU), в фикстуре прежний;
- `setuphost.exe` и `setup.exe` в результате — версии boot.wim (9725 и 9549);
- две строки `True`: `efisys.bin` и `bootmgr` не изменились;
- в `sources` результата файлов больше, чем в фикстуре (добавились файлы Setup DU).

### 5. Хост и журнал движка

```powershell
& $X mounts
& $X host-check
Get-ChildItem C:\ProgramData\ImageForge\logs\engine-*.log | Sort-Object LastWriteTime | Select-Object -Last 1 |
  Get-Content -Encoding UTF8 | Select-String "setup dynamic|setup du|expand.exe|boot file|setup file"
```
Ожидается: `mounts` → `none`, `host-check` → `RESULT: OK`. В журнале движка `plan setup dynamic update setup\…kb5128977… from C:\IF\upd` и строка `expand.exe …`.

### 6. Загрузка и установка (VirtualBox, UEFI и Secure Boot)

Сборка ISO на сервисной VM:
```powershell
$O = "C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\amd64\Oscdimg\oscdimg.exe"
Test-Path $R\boot\etfsboot.com, $R\efi\microsoft\boot\efisys.bin
& $O -m -o -u2 -udfver102 "-bootdata:2#p0,e,b$R\boot\etfsboot.com#pEF,e,b$R\efi\microsoft\boot\efisys.bin" $R C:\IF\ImageForge-media.iso
& $X hash C:\IF\ImageForge-media.iso
Stop-Transcript
```
Перенести `C:\IF\ImageForge-media.iso` на хост. Создать машину как в шаге 2 [M1-INSTALL](M1-INSTALL.md): Windows 11 (64-bit), «Пропустить автоматическую установку», 4 ГБ, 2 процессора, диск 64 ГБ, EFI, Secure Boot, TPM 2.0. Загрузиться с ISO и установить Pro до рабочего стола.
Ожидается: загрузка с ISO при Secure Boot проходит, программа установки стартует без ошибки, установка доходит до рабочего стола.

## Что прислать

- файл `C:\IF\s2-media-ui.txt`;
- снимок превью из шага 2;
- строки журнала окна из шага 3;
- итог шага 6 (дошла ли установка до рабочего стола; при ошибке — снимок экрана);
- при ошибке: текст статуса, журнал окна целиком и последний `C:\ProgramData\ImageForge\logs\engine-*.log`.

## Результаты

| Сборка | VM | Шаги | Итог |
|---|---|---|---|
