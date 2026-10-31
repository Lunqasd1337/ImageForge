# Ручная проверка S2-TARGET-DRIVERS-UI: драйверы в WinRE и boot.wim

Статус: **Ожидает проверки**.
Проверяет пункт 4 сверки с планом (раздел 12): драйверы попадают не только в install.wim, но и в WinRE и во все образы boot.wim.
- У каждого найденного пакета три отметки: «В образ», «В WinRE», «В boot.wim».
- Превью и статус показывают драйверы WinRE и boot.wim.
- Пакеты копируются в рабочую папку и сверяются с хешами плана, как пакеты для образа.
- Запуск добавляет драйверы в `mount-winre` и в каждый образ `mount-boot`: после обновлений, до очистки с /ResetBase.

Обновления в этом запуске не ставятся, папку обновлений указывать не нужно. Так запуск короче, и видно, что для одних драйверов она не требуется.

## Сборка

- Из одной сборки CI этого PR: `ImageForge.App-<sha>` и `ImageForge.Experiments-<sha>`.
- `C:\IF\app` удалить и распаковать туда новый `ImageForge.App`; `ImageForge.Experiments.exe` положить в `C:\IF`.

## Требования

Сервисная VM с контрольной точки `fixture-ready`, фикстура `C:\IF\fixture\media`, свободно не меньше 40 ГБ. ISO virtio-win подключён, как в [S2-DRIVERS-UI](S2-DRIVERS-UI.md); ниже он на `E:`.

PowerShell от администратора:
```powershell
$E = "C:\IF\ImageForge.Experiments.exe"; $X = { & $E @args | Out-Host }; $D = "E:"
Start-Transcript C:\IF\s2-target-drivers.txt
& $X version
& $X mounts
$P = "C:\IF\drv-t"; Remove-Item $P -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $P\viostor, $P\netkvm -Force | Out-Null
Copy-Item $D\viostor\w11\amd64\* $P\viostor; Copy-Item $D\NetKVM\w11\amd64\* $P\netkvm
Get-ChildItem $P -Recurse -File | ForEach-Object { $_.IsReadOnly = $false }
& $X driver-scan $P
```
Ожидается: `mounts` → `none`; `driver-scan` находит 2 INF без проблем.

## Шаги

### 1. Окно

Запустить `C:\IF\app\ImageForge.App.exe`, «Подключить движок», в UAC — «Да». Источник `C:\IF\fixture\media`, рабочая папка `C:\IF\ws-tdrv`, результат `C:\IF\result-tdrv`, «Редакция» — Pro. «Прочитать образ».
Ожидается: в журнале `протокол 15`.

### 2. Отметки и превью

Папка драйверов `C:\IF\drv-t`, «Найти пакеты», раскрыть «Найденные пакеты».
Ожидается:
- под каждым пакетом три отметки «В образ», «В WinRE», «В boot.wim»;
- статус «Найдено INF: 2, можно добавить: 2. Отмечено в образ: 0, в WinRE: 0, в boot.wim: 0.».

Отметить у `viostor\viostor.inf` «В WinRE» и «В boot.wim», у `netkvm\netkvm.inf` только «В boot.wim». «В образ» не ставить. «Подготовить».
Ожидается:
- статус пакетов «…Отмечено в образ: 0, в WinRE: 1, в boot.wim: 2.»;
- в превью блок «WinRE (…Winre.wim): извлечь, добавить выбранные обновления и драйверы…», под ним «Добавить в WinRE драйвер viostor\viostor.inf: SCSIAdapter, Red Hat, Inc., …»;
- блок «boot.wim (sources\boot.wim): добавить в каждый образ…», под ним строки «Добавить в boot.wim драйвер netkvm\netkvm.inf: Net, …» и «…viostor\viostor.inf: SCSIAdapter, …»;
- строки «Добавить драйвер» (в образ) нет;
- статус «План готов… В WinRE обновлений: 0, драйверов: 1. В boot.wim обновлений: 0, драйверов: 2. …».

Пришлите снимок превью.

### 3. Запуск

«Запустить», окно не закрывать (около 15–20 минут).
Ожидается в журнале:
- `staged 2 driver package(s)` до `mount...`;
- на шаге `winre...`: `winre packages: …`, `winre add driver viostor\viostor.inf`, `winre cleanup component store (ResetBase)`, `winre: … MB -> commit … MB -> export … MB, put back into the image`;
- на шаге `boot...`: для `boot image 1 of 2` и `boot image 2 of 2` по строке `boot add driver netkvm\netkvm.inf` и `boot add driver viostor\viostor.inf`, затем `boot cleanup: packages … -> …`;
- `export boot image 2 of 2 (bootable)`, `boot.wim: … MB -> … MB, boot index 2`;
- «Готово. Результат сохранён.».

Перепишите строки `winre…`, `boot…` и время шагов `winre` и `boot`.

### 4. Сверка результата

Номер `$I` — номер Pro в списке «Редакция» (в фикстуре 3).
```powershell
$I = 3; $S = "C:\IF\scratch"; $R = "C:\IF\result-tdrv"
New-Item -ItemType Directory $S\mount -Force | Out-Null
& $X mount $R\sources\install.wim $I $S\mount
& $X driver-list $S\mount third-party
Copy-Item -Force $S\mount\Windows\System32\Recovery\Winre.wim $S\winre-t.wim
& $X unmount $S\mount discard
Set-ItemProperty $S\winre-t.wim -Name Attributes -Value Normal
& $X mount $S\winre-t.wim 1 $S\mount
& $X driver-list $S\mount third-party
& $X unmount $S\mount discard
Copy-Item -Force $R\sources\boot.wim $S\boot-t.wim
Set-ItemProperty $S\boot-t.wim -Name Attributes -Value Normal
foreach ($b in 1, 2) {
  & $X mount $S\boot-t.wim $b $S\mount
  & $X driver-list $S\mount third-party
  & $X unmount $S\mount discard
}
& $X wim-header $R\sources\boot.wim
Remove-Item $S\winre-t.wim, $S\boot-t.wim -Force
```
Ожидается:
- в самом образе сторонних драйверов нет;
- в WinRE один: viostor, `Signed | BOOT-CRITICAL`;
- в каждом из двух образов boot.wim два: netkvm и viostor;
- `wim-header`: 2 образа, boot index 2.

### 5. Хост и журнал движка

```powershell
& $X mounts
& $X host-check
Get-ChildItem C:\ProgramData\ImageForge\logs\engine-*.log | Sort-Object LastWriteTime | Select-Object -Last 1 |
  Get-Content -Encoding UTF8 | Select-String "WinRE driver|boot.wim driver|DismAddDriver|add driver"
Stop-Transcript
```
Ожидается: `mounts` → `none`, `host-check` → `RESULT: OK`. В журнале движка `plan add WinRE driver viostor\viostor.inf from C:\IF\drv-t`, два `plan add boot.wim driver …` и вызовы `DismAddDriver` с путями внутри `C:\IF\ws-tdrv\…\inputs\drivers` в `mount-winre` и `mount-boot`.

## Что прислать

- файл `C:\IF\s2-target-drivers.txt`;
- снимок превью из шага 2;
- строки журнала окна из шага 3;
- при ошибке: текст статуса, журнал окна целиком и последний `C:\ProgramData\ImageForge\logs\engine-*.log`.

## Результаты

| Сборка | VM | Шаги | Итог |
|---|---|---|---|
